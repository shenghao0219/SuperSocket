﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SuperSocket.ClientEngine
{
    public abstract class TcpClientSession : ClientSession
    {
        protected string HostName { get; private set; }

        public TcpClientSession()
            : base()
        {
        }

        public TcpClientSession(EndPoint remoteEndPoint)
            : base(remoteEndPoint)
        {
        }

        protected abstract void SocketEventArgsCompleted(object sender, SocketAsyncEventArgs e);

        public override void Connect()
        {
            var socketEventArgs = new SocketAsyncEventArgs();
            socketEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(SocketEventArgsCompleted);

#if SILVERLIGHT
            socketEventArgs.RemoteEndPoint = RemoteEndPoint;

            if (!Socket.ConnectAsync(SocketType.Stream, ProtocolType.Tcp, socketEventArgs))
                ProcessConnect(socketEventArgs);
#else
            if (RemoteEndPoint is IPEndPoint)
            {
                socketEventArgs.RemoteEndPoint = RemoteEndPoint;

                var ipEndPoint = RemoteEndPoint as IPEndPoint;
                HostName = ipEndPoint.Address.ToString();

                var socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socketEventArgs.UserToken = new ConnectStateToken
                {
                    Addresses = new IPAddress[] { ipEndPoint.Address },
                    Port = ipEndPoint.Port,
                    CurrentConnectIndex = 0,
                    Socket = socket,
                    SocketEventArgs = socketEventArgs
                };

                if (!socket.ConnectAsync(socketEventArgs))
                    ProcessConnect(socketEventArgs);
            }
            else if (RemoteEndPoint is DnsEndPoint)
            {
                var dnsEndPoint = RemoteEndPoint as DnsEndPoint;
                HostName = dnsEndPoint.Host;
                Dns.BeginGetHostAddresses(dnsEndPoint.Host, OnGetHostAddresses,
                    new ConnectStateToken
                    {
                        Port = dnsEndPoint.Port,
                        SocketEventArgs = socketEventArgs
                    });
            }
#endif
        }

#if !SILVERLIGHT
        private void OnGetHostAddresses(IAsyncResult state)
        {
            IPAddress[] addresses = Dns.EndGetHostAddresses(state);

            var connectState = state.AsyncState as ConnectStateToken;

            if (!Socket.OSSupportsIPv6)
                addresses = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();

            if (addresses.Length <= 0)
                return;

            var socketEventArgs = connectState.SocketEventArgs;

            connectState.Addresses = addresses;

            var ipEndPoint = new IPEndPoint(addresses[0], connectState.Port);
            socketEventArgs.RemoteEndPoint = ipEndPoint;

            var socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            connectState.Socket = socket;

            socketEventArgs.UserToken = connectState;

            if (!socket.ConnectAsync(socketEventArgs))
                ProcessConnect(socketEventArgs);
        }
#endif

        protected void ProcessConnect(SocketAsyncEventArgs e)
        {
#if SILVERLIGHT
            if (e.SocketError != SocketError.Success)
            {
                OnError(new SocketException((int)e.SocketError));
                return;
            }

            Client = e.ConnectSocket;
#else
            var connectState = e.UserToken as ConnectStateToken;

            if (e.SocketError != SocketError.Success)
            {
                if (e.SocketError != SocketError.HostUnreachable)
                {
                    OnError(new SocketException((int)e.SocketError));
                    return;
                }

                if (connectState.Addresses.Length <= (connectState.CurrentConnectIndex + 1))
                {
                    OnError(new SocketException((int)SocketError.HostUnreachable));
                    return;
                }

                var currentConnectIndex = connectState.CurrentConnectIndex + 1;
                var currentIpAddress = connectState.Addresses[currentConnectIndex];

                e.RemoteEndPoint = new IPEndPoint(currentIpAddress, connectState.Port);
                connectState.CurrentConnectIndex = currentConnectIndex;

                var socket = new Socket(currentIpAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                connectState.Socket = socket;

                if (!socket.ConnectAsync(e))
                    ProcessConnect(e);

                return;
            }

            Client = connectState.Socket;
            e.UserToken = null;
#endif
            StartReceive(e);
        }

        protected abstract void StartReceive(SocketAsyncEventArgs e);

        protected bool EnsureSocketClosed()
        {
            if (Client == null)
                return false;

            if (Client.Connected)
            {
                try
                {
                    Client.Shutdown(SocketShutdown.Both);
                    Client.Close();
                }
                catch
                {

                }
            }

            Client = null;
            return true;
        }

        private void DetectConnected()
        {
            if (Client != null)
                return;

            throw new Exception("The socket is not connected!", new SocketException((int)SocketError.NotConnected));
        }

        private ConcurrentQueue<ArraySegment<byte>> m_SendingQueue = new ConcurrentQueue<ArraySegment<byte>>();

        protected volatile bool IsSending = false;

        public override void Send(byte[] data, int offset, int length)
        {
            DetectConnected();

            m_SendingQueue.Enqueue(new ArraySegment<byte>(data, offset, length));

            if (!IsSending)
            {
                DequeueSend();
            }
        }

        public override void Send(IList<ArraySegment<byte>> segments)
        {
            DetectConnected();

            for (var i = 0; i < segments.Count; i++)
                m_SendingQueue.Enqueue(segments[i]);

            if (!IsSending)
            {
                DequeueSend();
            }
        }

        protected void DequeueSend()
        {
            IsSending = true;
            ArraySegment<byte> segment;

            if (!m_SendingQueue.TryDequeue(out segment))
            {
                IsSending = false;
                return;
            }

            SendInternal(segment);
        }

        protected abstract void SendInternal(ArraySegment<byte> segment);

        public override void Close()
        {
            if (EnsureSocketClosed())
                OnClosed();
        }
    }
}