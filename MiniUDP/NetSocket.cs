﻿/*
 *  MiniUDP - A Simple UDP Layer for Shipping and Receiving Byte Arrays
 *  Copyright (c) 2015-2016 - Alexander Shoulson - http://ashoulson.com
 *
 *  This software is provided 'as-is', without any express or implied
 *  warranty. In no event will the authors be held liable for any damages
 *  arising from the use of this software.
 *  Permission is granted to anyone to use this software for any purpose,
 *  including commercial applications, and to alter it and redistribute it
 *  freely, subject to the following restrictions:
 *  
 *  1. The origin of this software must not be misrepresented; you must not
 *     claim that you wrote the original software. If you use this software
 *     in a product, an acknowledgment in the product documentation would be
 *     appreciated but is not required.
 *  2. Altered source versions must be plainly marked as such, and must not be
 *     misrepresented as being the original software.
 *  3. This notice may not be removed or altered from any source distribution.
*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

using CommonTools;

namespace MiniUDP
{
  public delegate void PeerEvent(NetPeer source);

  public class NetSocket
  {
    public event PeerEvent Connected;

    private const int DATA_BUFFER_SIZE = 2048;
    private const int CONNECTION_RETRY_COUNT = 20;
    private const double CONNECTION_RETRY_RATE = 0.5;

    private class PendingConnection
    {
      internal IPEndPoint EndPoint { get { return this.endPoint; } }

      private readonly IPEndPoint endPoint;
      private double lastAttempt;

      internal PendingConnection(IPEndPoint endPoint)
      {
        this.endPoint = endPoint;
        this.lastAttempt = double.NegativeInfinity;
      }

      internal void LogAttempt()
      {
        this.lastAttempt = NetTime.Time;
      }

      internal bool TimeToRetry()
      {
        double nextTime = this.lastAttempt + NetSocket.CONNECTION_RETRY_RATE;
        return nextTime < NetTime.Time;
      }
    }

    #region Static Methods
    public static IPEndPoint StringToEndPoint(string address)
    {
      string[] split = address.Split(':');
      string stringAddress = split[0];
      string stringPort = split[1];

      int port = int.Parse(stringPort);
      IPAddress ipaddress = IPAddress.Parse(stringAddress);
      IPEndPoint endpoint = new IPEndPoint(ipaddress, port);

      if (endpoint == null) 
        throw new ArgumentException("Failed to parse address: " + address);
      return endpoint;
    }

    public static IPAddress StringToAddress(string address)
    {
      string[] split = address.Split(':');
      string stringAddress = split[0];
      string stringPort = split[1];

      int port = int.Parse(stringPort);
      IPAddress ipaddress = IPAddress.Parse(stringAddress);

      if (ipaddress == null)
        throw new ArgumentException("Failed to parse address: " + address);
      return ipaddress;
    }
    #endregion

    #region Properties and Fields
    public bool UseWhiteList { get; set; }

    protected Socket socket;

    private byte[] dataBuffer;
    private GenericPool<NetPacket> packetPool;
    private Dictionary<IPEndPoint, NetPeer> peers;
    private Dictionary<IPEndPoint, PendingConnection> pending;
    private HashSet<IPAddress> whiteList;
    #endregion

    #region Constructors
    public NetSocket()
    {
      this.dataBuffer = new byte[NetSocket.DATA_BUFFER_SIZE];
      this.packetPool = new GenericPool<NetPacket>();
      this.peers = new Dictionary<IPEndPoint, NetPeer>();
      this.pending = new Dictionary<IPEndPoint, PendingConnection>();
      this.whiteList = new HashSet<IPAddress>();

      this.socket =
        new Socket(
          AddressFamily.InterNetwork,
          SocketType.Dgram,
          ProtocolType.Udp);

      this.socket.ReceiveBufferSize = NetSocket.DATA_BUFFER_SIZE;
      this.socket.SendBufferSize = NetSocket.DATA_BUFFER_SIZE;
      this.socket.Blocking = false;
    }
    #endregion

    #region Public Interface
    /// <summary>
    /// Adds an address to the whitelist for refusing connections.
    /// </summary>
    public void AddToWhiteList(string address)
    {
      this.whiteList.Add(NetSocket.StringToAddress(address));
    }

    /// <summary>
    /// Starts the socket using the supplied endpoint.
    /// If the port is taken, the given port will be incremented to a free port.
    /// </summary>
    public void Bind(int port)
    {
      try
      {
        this.socket.Bind(new IPEndPoint(IPAddress.Any, port));
      }
      catch (SocketException exception)
      {
        if (exception.ErrorCode == 10048)
          NetDebug.LogError("Port " + port + " unavailable!");
        else
          NetDebug.LogError(exception.Message);
        return;
      }
    }

    /// <summary>
    /// Begins the process of connecting to an address given as a string in 
    /// "IP.IP.IP.IP:PORT" format.
    /// </summary>
    public void Connect(string address)
    {
      IPEndPoint endPoint = NetSocket.StringToEndPoint(address);
      if (this.peers.ContainsKey(endPoint) == false)
        this.pending.Add(endPoint, new PendingConnection(endPoint));
    }

    /// <summary>
    /// Called at the beginning of processing. Reads all incoming data,
    /// processes it, and assigns packets to the peers it received them from.
    /// </summary>
    public void Poll()
    {
      while (this.CanReceive())
      {
        IPEndPoint source;
        NetPacket packet = this.TryReceive(out source);

        if ((packet != null) && this.PreProcess(packet, source))
        {
          NetPeer sourcePeer;
          if (this.peers.TryGetValue(source, out sourcePeer))
            sourcePeer.QueueReceived(packet);
          else
            NetDebug.LogWarning("Message from unrecognized peer: " + source);
        }
      }

      // We're done receiving everything, so flag peers if applicable
      foreach (NetPeer peer in this.peers.Values)
        peer.FlagMessagesWaiting();
    }

    /// <summary>
    /// Called at the end of processing, sends any packets that have been
    /// queued up for transmission to all relevant peers.
    /// </summary>
    public void Transmit()
    {
      this.RetryConnections();

      foreach (NetPeer peer in this.peers.Values)
      {
        foreach (NetPacket packet in peer.Outgoing)
          this.TrySend(packet, peer.EndPoint);

        // Clean all incoming and outgoing packets
        peer.ClearOutgoing();
        peer.ClearReceived();
      }
    }
    #endregion

    #region Protected Helpers
    internal NetPacket AllocatePacket(
      NetPacketType packetType = NetPacketType.Message)
    {
      NetPacket packet = this.packetPool.Allocate();
      packet.Initialize(packetType);
      return packet;
    }

    private void RetryConnections()
    {
      foreach (PendingConnection pending in this.pending.Values)
      {
        if (pending.TimeToRetry())
        {
          this.TrySend(
            this.AllocatePacket(NetPacketType.Connect),
            pending.EndPoint);
          pending.LogAttempt();
        }
      }
    }

    private bool PreProcess(NetPacket packet, IPEndPoint source)
    {
      switch (packet.PacketType)
      {
        case NetPacketType.Connect:
          this.HandleConnection(source, true);
          return false;

        case NetPacketType.Connected:
          this.HandleConnection(source, false);
          return false;

        case NetPacketType.Message:
          return true;

        default:
          NetDebug.LogWarning("Invalid packet type for server");
          return false;
      }
    }

    private void HandleConnection(IPEndPoint source, bool sendResponse)
    {
      if (this.VerifySource(source))
      {
        NetPeer peer = this.RegisterPeer(source);

        if (sendResponse)
          peer.QueueOutgoing(this.AllocatePacket(NetPacketType.Connected));

        if (this.pending.ContainsKey(source))
          this.pending.Remove(source);
      }
    }

    private bool VerifySource(IPEndPoint source)
    {
      if (this.UseWhiteList == false)
        return true;

      if (this.whiteList.Contains(source.Address))
        return true;

      NetDebug.LogWarning("Unrecognized connection: " + source.Address);
      return false;
    }

    private NetPeer RegisterPeer(IPEndPoint source)
    {
      NetPeer peer = this.GetPeer(source);
      if (peer == null)
      {
        peer = new NetPeer(source, this);
        this.AddPeer(peer);

        if (this.Connected != null)
          this.Connected.Invoke(peer);
      }

      return peer;
    }

    private void AddPeer(NetPeer peer)
    {
      this.peers.Add(peer.EndPoint, peer);
    }

    private NetPeer GetPeer(IPEndPoint endPoint)
    {
      NetPeer peer = null;
      if (this.peers.TryGetValue(endPoint, out peer))
        return peer;
      return null;
    }

    /// <summary> 
    /// Returns true if OS socket has data available for read. 
    /// </summary>
    private bool CanReceive()
    {
      return this.socket.Poll(0, SelectMode.SelectRead);
    }

    /// <summary> 
    /// Attempts to read from OS socket. Returns false if the read fails
    /// or if there is nothing to read.
    /// </summary>
    private NetPacket TryReceive(out IPEndPoint source)
    {
      source = null;

      try
      {
        EndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
        int receiveCount = 
          this.socket.ReceiveFrom(
            this.dataBuffer,
            this.dataBuffer.Length, 
            SocketFlags.None, 
            ref endPoint);

        if (receiveCount > 0)
        {
          source = endPoint as IPEndPoint;
          NetPacket packet = this.packetPool.Allocate();
          if (packet.NetInput(this.dataBuffer, receiveCount))
            return packet;
        }

        return null;
      }
      catch
      {
        return null;
      }
    }

    /// <summary> 
    /// Attempts to send data to endpoint via OS socket. 
    /// Returns false if the send failed.
    /// </summary>
    private bool TrySend(
      NetPacket packet,
      IPEndPoint destination)
    {
      try
      {
        int bytesToSend = packet.NetOutput(this.dataBuffer);
        int bytesSent = 
          this.socket.SendTo(
            this.dataBuffer,
            bytesToSend, 
            SocketFlags.None, 
            destination);

        return (bytesSent == bytesToSend);
      }
      catch
      {
        return false;
      }
    }
    #endregion
  }
}