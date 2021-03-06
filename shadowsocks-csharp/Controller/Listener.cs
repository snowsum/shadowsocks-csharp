﻿using Shadowsocks.Model;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Timers;

namespace Shadowsocks.Controller
{
    public class Listener
    {
        public interface Service
        {
            bool Handle(byte[] firstPacket, int length, Socket socket);
        }

        Configuration _config;
        bool _shareOverLAN;
        Socket _socket;
        Socket _socket_v6;
        IList<Service> _services;
        protected System.Timers.Timer timer;
        protected object timerLock = new object();

        public Listener(IList<Service> services)
        {
            this._services = services;
        }

        public IList<Service> GetServices()
        {
            return _services;
        }

        private bool CheckIfPortInUse(int port)
        {
            IPGlobalProperties ipProperties = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] ipEndPoints = ipProperties.GetActiveTcpListeners();

            foreach (IPEndPoint endPoint in ipEndPoints)
            {
                if (endPoint.Port == port)
                {
                    return true;
                }
            }
            return false;
        }

        public bool isConfigChange(Configuration config)
        {
            if (this._shareOverLAN != config.shareOverLan
                || _socket == null
                || ((IPEndPoint)_socket.LocalEndPoint).Port != config.localPort)
            {
                return true;
            }
            return false;
        }

        public void Start(Configuration config)
        {
            this._config = config;
            this._shareOverLAN = config.shareOverLan;

            if (CheckIfPortInUse(_config.localPort))
                throw new Exception(I18N.GetString("Port already in use"));

            try
            {
                // Create a TCP/IP socket.
                bool ipv6 = true;
                //bool ipv6 = false;
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (ipv6)
                {
                    try
                    {
                        _socket_v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                        _socket_v6.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    }
                    catch
                    {
                        _socket_v6 = null;
                    }
                }
                IPEndPoint localEndPoint = null;
                IPEndPoint localEndPointV6 = null;
                if (_shareOverLAN)
                {
                    localEndPoint = new IPEndPoint(IPAddress.Any, _config.localPort);
                    localEndPointV6 = new IPEndPoint(IPAddress.IPv6Any, _config.localPort);
                }
                else
                {
                    localEndPoint = new IPEndPoint(IPAddress.Loopback, _config.localPort);
                    localEndPointV6 = new IPEndPoint(IPAddress.IPv6Loopback, _config.localPort);
                }

                // Bind the socket to the local endpoint and listen for incoming connections.
                _socket.Bind(localEndPoint);
                _socket.Listen(1024);
                if (_socket_v6 != null)
                {
                    _socket_v6.Bind(localEndPointV6);
                    _socket_v6.Listen(1024);
                }


                // Start an asynchronous socket to listen for connections.
                Console.WriteLine("ShadowsocksR started");
                _socket.BeginAccept(
                    new AsyncCallback(AcceptCallback),
                    _socket);
                if (_socket_v6 != null)
                    _socket_v6.BeginAccept(
                        new AsyncCallback(AcceptCallback),
                        _socket_v6);
            }
            catch (SocketException)
            {
                _socket.Close();
                if (_socket_v6 != null)
                    _socket_v6.Close();
                throw;
            }
        }

        public void Stop()
        {
            ResetTimeout(0, null);
            if (_socket != null)
            {
                _socket.Close();
                _socket = null;
            }
            if (_socket_v6 != null)
            {
                _socket_v6.Close();
                _socket_v6 = null;
            }
        }

        private void ResetTimeout(Double time, Socket socket)
        {
            if (time <= 0 && timer == null)
                return;

            lock (timerLock)
            {
                if (time <= 0)
                {
                    if (timer != null)
                    {
                        timer.Enabled = false;
                        timer.Elapsed -= (sender, e) => timer_Elapsed(sender, e, socket);
                        timer.Dispose();
                        timer = null;
                    }
                }
                else
                {
                    if (timer == null)
                    {
                        timer = new System.Timers.Timer(time * 1000.0);
                        timer.Elapsed += (sender, e) => timer_Elapsed(sender, e, socket);
                        timer.Start();
                    }
                    else
                    {
                        timer.Interval = time * 1000.0;
                        timer.Stop();
                        timer.Start();
                    }
                }
            }
        }

        private void timer_Elapsed(object sender, ElapsedEventArgs eventArgs, Socket socket)
        {
            if (timer == null)
            {
                return;
            }
            Socket listener = socket;
            try
            {
                listener.BeginAccept(
                    new AsyncCallback(AcceptCallback),
                    listener);
                ResetTimeout(0, listener);
            }
            catch (ObjectDisposedException)
            {
                // do nothing
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
                ResetTimeout(5, listener);
            }
        }


        protected bool isMatchSubNet(IPAddress ip, IPAddress net, int netmask)
        {
            byte[] addr = ip.GetAddressBytes();
            byte[] net_addr = net.GetAddressBytes();
            int i = 8, index = 0;
            for (; i < netmask; i += 8, index += 1)
            {
                if (addr[index] != net_addr[index])
                    return false;
            }
            if ((addr[index] >> (i - netmask)) != (net_addr[index] >> (i - netmask)))
                return false;
            return true;
        }

        protected bool isMatchSubNet(IPAddress ip, string netmask)
        {
            string[] mask = netmask.Split('/');
            IPAddress netmask_ip = IPAddress.Parse(mask[0]);
            if (ip.AddressFamily == netmask_ip.AddressFamily)
            {
                try
                {
                    return isMatchSubNet(ip, netmask_ip, Convert.ToInt16(mask[1]));
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        protected bool isLAN(Socket socket)
        {
            IPAddress ip = ((IPEndPoint)socket.RemoteEndPoint).Address;
            byte[] addr = ((IPEndPoint)socket.RemoteEndPoint).Address.GetAddressBytes();
            if (addr.Length == 4)
            {
                string[] netmasks = new string[]
                {
                    "0.0.0.0/8",
                    "10.0.0.0/8",
                    //"100.64.0.0/10", //部分地区运营商貌似在使用这个，这个可能不安全
                    "127.0.0.0/8",
                    "169.254.0.0/16",
                    "172.16.0.0/12",
                    "192.0.0.0/24",
                    "192.0.2.0/24",
                    "192.168.0.0/16",
                    "198.18.0.0/15",
                    "198.51.100.0/24",
                    "203.0.113.0/24",
                    "::1/128",
                    "fc00::/7",
                    "fe80::/10"
                };
                foreach (string netmask in netmasks)
                {
                    if (isMatchSubNet(ip, netmask))
                        return true;
                }
                //if (addr[0] == 10) return true;
                //if (addr[0] == 127 && addr[1] == 0 && addr[2] == 0) return true;
                //if (addr[0] == 172 && addr[1] >= 16 && addr[1] <= 31) return true;
                //if (addr[0] == 192 && addr[1] >= 168) return true;
                return false;
            }
            return true;
        }

        public void AcceptCallback(IAsyncResult ar)
        {
            Socket listener = (Socket)ar.AsyncState;
            try
            {
                Socket conn = listener.EndAccept(ar);

                if (!isLAN(conn))
                {
                    conn.Shutdown(SocketShutdown.Both);
                    conn.Close();
                }
                else
                {
                    byte[] buf = new byte[4096];
                    object[] state = new object[] {
                        conn,
                        buf
                    };

                    conn.BeginReceive(buf, 0, buf.Length, 0,
                        new AsyncCallback(ReceiveCallback), state);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            finally
            {
                try
                {
                    listener.BeginAccept(
                        new AsyncCallback(AcceptCallback),
                        listener);
                }
                catch (ObjectDisposedException)
                {
                    // do nothing
                }
                catch (Exception e)
                {
                    Logging.LogUsefulException(e);
                    ResetTimeout(5, listener);
                }
            }
        }


        private void ReceiveCallback(IAsyncResult ar)
        {
            object[] state = (object[])ar.AsyncState;

            Socket conn = (Socket)state[0];
            byte[] buf = (byte[])state[1];
            try
            {
                int bytesRead = conn.EndReceive(ar);
                foreach (Service service in _services)
                {
                    if (service.Handle(buf, bytesRead, conn))
                    {
                        return;
                    }
                }
                // no service found for this
                // shouldn't happen
                conn.Shutdown(SocketShutdown.Both);
                conn.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                conn.Shutdown(SocketShutdown.Both);
                conn.Close();
            }
        }
    }
}
