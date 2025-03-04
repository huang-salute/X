﻿using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using NewLife.Caching;
using NewLife.Log;
using NewLife.Net;

namespace NewLife;

/// <summary>网络工具类</summary>
public static class NetHelper
{
    #region 属性
    private static readonly ICache _Cache = MemoryCache.Instance;
    #endregion

    #region 构造
    static NetHelper()
    {
        // 网络有变化时，清空所有缓存
        NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
    }

    private static void NetworkChange_NetworkAvailabilityChanged(Object sender, NetworkAvailabilityEventArgs e) => _Cache.Clear();

    private static void NetworkChange_NetworkAddressChanged(Object sender, EventArgs e) => _Cache.Clear();
    #endregion

    #region 辅助函数
    /// <summary>设置超时检测时间和检测间隔</summary>
    /// <param name="socket">要设置的Socket对象</param>
    /// <param name="iskeepalive">是否启用Keep-Alive</param>
    /// <param name="starttime">多长时间后开始第一次探测（单位：毫秒）</param>
    /// <param name="interval">探测时间间隔（单位：毫秒）</param>
    public static void SetTcpKeepAlive(this Socket socket, Boolean iskeepalive, Int32 starttime = 10000, Int32 interval = 10000)
    {
        if (socket == null || !socket.Connected) return;
        UInt32 dummy = 0;
        var inOptionValues = new Byte[Marshal.SizeOf(dummy) * 3];
        BitConverter.GetBytes((UInt32)(iskeepalive ? 1 : 0)).CopyTo(inOptionValues, 0);
        BitConverter.GetBytes((UInt32)starttime).CopyTo(inOptionValues, Marshal.SizeOf(dummy));
        BitConverter.GetBytes((UInt32)interval).CopyTo(inOptionValues, Marshal.SizeOf(dummy) * 2);

#if !NETFRAMEWORK
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
#endif
    }

    /// <summary>分析地址，根据IP或者域名得到IP地址，缓存60秒，异步更新</summary>
    /// <param name="hostname"></param>
    /// <returns></returns>
    public static IPAddress ParseAddress(this String hostname)
    {
        if (hostname.IsNullOrEmpty()) return null;

        var key = $"NetHelper:ParseAddress:{hostname}";
        if (_Cache.TryGetValue<IPAddress>(key, out var address)) return address;

        address = NetUri.ParseAddress(hostname)?.FirstOrDefault();

        _Cache.Set(key, address, 60);

        return address;
    }

    /// <summary>分析网络终结点</summary>
    /// <param name="address">地址，可以不带端口</param>
    /// <param name="defaultPort">地址不带端口时指定的默认端口</param>
    /// <returns></returns>
    public static IPEndPoint ParseEndPoint(String address, Int32 defaultPort = 0)
    {
        if (String.IsNullOrEmpty(address)) return null;

        var p = address.IndexOf("://");
        if (p >= 0) address = address[(p + 3)..];

        p = address.LastIndexOf(':');
        return p > 0
            ? new IPEndPoint(address[..p].ParseAddress(), Int32.Parse(address[(p + 1)..]))
            : new IPEndPoint(address.ParseAddress(), defaultPort);
    }

    /// <summary>针对IPv4和IPv6获取合适的Any地址</summary>
    /// <remarks>除了Any地址以为，其它地址不具备等效性</remarks>
    /// <param name="address"></param>
    /// <param name="family"></param>
    /// <returns></returns>
    public static IPAddress GetRightAny(this IPAddress address, AddressFamily family)
    {
        if (address.AddressFamily == family) return address;

        switch (family)
        {
            case AddressFamily.InterNetwork:
                if (address == IPAddress.IPv6Any) return IPAddress.Any;
                break;
            case AddressFamily.InterNetworkV6:
                if (address == IPAddress.Any) return IPAddress.IPv6Any;
                break;
            default:
                break;
        }
        return null;
    }

    /// <summary>是否Any地址，同时处理IPv4和IPv6</summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public static Boolean IsAny(this IPAddress address) => IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address);

    /// <summary>是否Any结点</summary>
    /// <param name="endpoint"></param>
    /// <returns></returns>
    public static Boolean IsAny(this EndPoint endpoint) => endpoint is IPEndPoint ep && (ep.Port == 0 || ep.Address.IsAny());

    /// <summary>是否IPv4地址</summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public static Boolean IsIPv4(this IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>是否本地地址</summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public static Boolean IsLocal(this IPAddress address) => IPAddress.IsLoopback(address) || GetIPsWithCache().Any(ip => ip.Equals(address));

    /// <summary>获取相对于指定远程地址的本地地址</summary>
    /// <param name="address"></param>
    /// <param name="remote"></param>
    /// <returns></returns>
    public static IPAddress GetRelativeAddress(this IPAddress address, IPAddress remote)
    {
        // 如果不是任意地址，直接返回
        var addr = address;
        if (addr == null || !addr.IsAny()) return addr;

        // 如果是本地环回地址，返回环回地址
        if (IPAddress.IsLoopback(remote)) return addr.IsIPv4() ? IPAddress.Loopback : IPAddress.IPv6Loopback;

        // 否则返回本地第一个IP地址
        foreach (var item in GetIPsWithCache())
            if (item.AddressFamily == addr.AddressFamily) return item;
        return null;
    }

    /// <summary>获取相对于指定远程地址的本地地址</summary>
    /// <param name="local"></param>
    /// <param name="remote"></param>
    /// <returns></returns>
    public static IPEndPoint GetRelativeEndPoint(this IPEndPoint local, IPAddress remote)
    {
        if (local == null || remote == null) return local;

        var addr = local.Address.GetRelativeAddress(remote);
        return addr == null ? local : new IPEndPoint(addr, local.Port);
    }

    /// <summary>指定地址的指定端口是否已被使用，似乎没办法判断IPv6地址</summary>
    /// <param name="protocol"></param>
    /// <param name="address"></param>
    /// <param name="port"></param>
    /// <returns></returns>
    public static Boolean CheckPort(this IPAddress address, NetType protocol, Int32 port)
    {
        //if (NewLife.Runtime.Mono) return false;
        if (!Runtime.Windows) return false;

        try
        {
            // 某些情况下检查端口占用会抛出异常，原因未知
            var gp = IPGlobalProperties.GetIPGlobalProperties();

            IPEndPoint[] eps = null;
            switch (protocol)
            {
                case NetType.Tcp:
                    eps = gp.GetActiveTcpListeners();
                    break;
                case NetType.Udp:
                    eps = gp.GetActiveUdpListeners();
                    break;
                default:
                    return false;
            }

            foreach (var item in eps)
                // 先比较端口，性能更好
                if (item.Port == port && item.Address.Equals(address)) return true;
        }
        catch (Exception ex)
        {
            XTrace.WriteException(ex);
        }

        return false;
    }

    /// <summary>检查该协议的地址端口是否已经呗使用</summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    public static Boolean CheckPort(this NetUri uri) => uri.Address.CheckPort(uri.Type, uri.Port);

    /// <summary>获取所有Tcp连接，带进程Id</summary>
    /// <returns></returns>
    public static TcpConnectionInformation2[] GetAllTcpConnections() => !Runtime.Windows ? new TcpConnectionInformation2[0] : TcpConnectionInformation2.GetAllTcpConnections();
    #endregion

    #region 本机信息
    /// <summary>获取活动的接口信息</summary>
    /// <returns></returns>
    public static IEnumerable<IPInterfaceProperties> GetActiveInterfaces()
    {
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (item.OperationalStatus != OperationalStatus.Up) continue;
            if (item.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var ip = item.GetIPProperties();
            if (ip != null) yield return ip;
        }
    }

    /// <summary>获取可用的DHCP地址</summary>
    /// <returns></returns>
    public static IEnumerable<IPAddress> GetDhcps()
    {
        var list = new List<IPAddress>();
        foreach (var item in GetActiveInterfaces())
        {
#if NET5_0_OR_GREATER
            if (item != null && !OperatingSystem.IsMacOS() && item.DhcpServerAddresses.Count > 0)
            {
                foreach (var elm in item.DhcpServerAddresses)
                {
                    if (list.Contains(elm)) continue;
                    list.Add(elm);

                    yield return elm;
                }
            }
#else
            if (item != null && item.DhcpServerAddresses.Count > 0)
            {
                foreach (var elm in item.DhcpServerAddresses)
                {
                    if (list.Contains(elm)) continue;
                    list.Add(elm);

                    yield return elm;
                }
            }
#endif
        }
    }

    /// <summary>获取可用的DNS地址</summary>
    /// <returns></returns>
    public static IEnumerable<IPAddress> GetDns()
    {
        var list = new List<IPAddress>();
        foreach (var item in GetActiveInterfaces())
            if (item != null && item.DnsAddresses.Count > 0)
                foreach (var elm in item.DnsAddresses)
                {
                    if (list.Contains(elm)) continue;
                    list.Add(elm);

                    yield return elm;
                }
    }

    /// <summary>获取可用的网关地址</summary>
    /// <returns></returns>
    public static IEnumerable<IPAddress> GetGateways()
    {
        var list = new List<IPAddress>();
        foreach (var item in GetActiveInterfaces())
            if (item != null && item.GatewayAddresses.Count > 0)
                foreach (var elm in item.GatewayAddresses)
                {
                    if (list.Contains(elm.Address)) continue;
                    list.Add(elm.Address);

                    yield return elm.Address;
                }
    }

    /// <summary>获取可用的IP地址</summary>
    /// <returns></returns>
    public static IEnumerable<IPAddress> GetIPs()
    {
        var dic = new Dictionary<UnicastIPAddressInformation, Int32>();
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (item.OperationalStatus != OperationalStatus.Up)
                continue;
            if (item.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var ipp = item.GetIPProperties();
            if (ipp != null && ipp.UnicastAddresses.Count > 0)
            {
                var gw = 0;

#if NET5_0_OR_GREATER
                if (!OperatingSystem.IsAndroid())
                {
                    gw = ipp.GatewayAddresses.Count;
                }
#else
                gw = ipp.GatewayAddresses.Count;
#endif

                foreach (var elm in ipp.UnicastAddresses)
                {
#if NET5_0_OR_GREATER
                    try
                    {
                        if (OperatingSystem.IsWindows() &&
                            elm.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred)
                            continue;
                    }
                    catch { }
#endif

                    dic.Add(elm, gw);
                }
            }
        }

        // 带网关的接口地址很重要，优先返回
        // Linux下不支持PrefixOrigin
        var ips = dic.OrderByDescending(e => e.Value)
            //.ThenByDescending(e => e.Key.PrefixOrigin == PrefixOrigin.Dhcp || e.Key.PrefixOrigin == PrefixOrigin.Manual)
            .Select(e => e.Key.Address).ToList();

        return ips;
    }

    /// <summary>获取本机可用IP地址，缓存60秒，异步更新</summary>
    /// <returns></returns>
    public static IPAddress[] GetIPsWithCache()
    {
        var key = $"NetHelper:GetIPsWithCache";
        if (_Cache.TryGetValue<IPAddress[]>(key, out var addrs)) return addrs;

        addrs = GetIPs().ToArray();

        _Cache.Set(key, addrs, 60);

        return addrs;
    }

    /// <summary>获取可用的多播地址</summary>
    /// <returns></returns>
    public static IEnumerable<IPAddress> GetMulticasts()
    {
        var list = new List<IPAddress>();
        foreach (var item in GetActiveInterfaces())
            if (item != null && item.MulticastAddresses.Count > 0)
                foreach (var elm in item.MulticastAddresses)
                {
                    if (list.Contains(elm.Address)) continue;
                    list.Add(elm.Address);

                    yield return elm.Address;
                }
    }

    private static readonly String[] _Excludes = new[] { "Loopback", "VMware", "VBox", "Virtual", "Teredo", "Microsoft", "VPN", "VNIC", "IEEE" };
    /// <summary>获取所有物理网卡MAC地址</summary>
    /// <returns></returns>
    public static IEnumerable<Byte[]> GetMacs()
    {
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            // 只要物理网卡
            if (item.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                NetworkInterfaceType.Tunnel or
                NetworkInterfaceType.Unknown) continue;
            if (_Excludes.Any(e => item.Description.Contains(e))) continue;
            if (Runtime.Windows && item.Speed < 1_000_000) continue;

            // 物理网卡在禁用时没有IP，如果有IP，则不能是环回
            var ips = item.GetIPProperties();
            var addrs = ips.UnicastAddresses
                .Where(e => e.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(e => e.Address)
                .ToArray();
            if (addrs.Length > 0 && addrs.All(e => IPAddress.IsLoopback(e))) continue;

            var mac = item.GetPhysicalAddress()?.GetAddressBytes();
            if (mac != null && mac.Length == 6) yield return mac;
        }
    }

    /// <summary>获取网卡MAC地址（网关所在网卡）</summary>
    /// <returns></returns>
    public static Byte[] GetMac()
    {
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (_Excludes.Any(e => item.Description.Contains(e))) continue;
            if (Runtime.Windows && item.Speed < 1_000_000) continue;

            var ips = item.GetIPProperties();
            var addrs = ips.UnicastAddresses
                .Where(e => e.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(e => e.Address)
                .ToArray();
            if (addrs.All(e => IPAddress.IsLoopback(e))) continue;

            // 网关
            addrs = ips.GatewayAddresses
                .Where(e => e.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(e => e.Address)
                .ToArray();
            if (addrs.Length == 0) continue;

            var mac = item.GetPhysicalAddress()?.GetAddressBytes();
            if (mac != null && mac.Length == 6) return mac;
        }

        return null;
    }

    /// <summary>获取本地第一个IPv4地址</summary>
    /// <returns></returns>
    public static IPAddress MyIP() => GetIPsWithCache().FirstOrDefault(ip => ip.IsIPv4() && !IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] != 169);

    /// <summary>获取本地第一个IPv6地址</summary>
    /// <returns></returns>
    public static IPAddress MyIPv6() => GetIPsWithCache().FirstOrDefault(ip => !ip.IsIPv4() && !IPAddress.IsLoopback(ip));
    #endregion

    #region 远程开机
    /// <summary>唤醒指定MAC地址的计算机</summary>
    /// <param name="macs"></param>
    public static void Wake(params String[] macs)
    {
        if (macs == null || macs.Length <= 0) return;

        foreach (var item in macs)
            Wake(item);
    }

    private static void Wake(String mac)
    {
        mac = mac.Replace("-", null).Replace(":", null);
        var buffer = new Byte[mac.Length / 2];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = Byte.Parse(mac.Substring(i * 2, 2), NumberStyles.HexNumber);

        var bts = new Byte[6 + 16 * buffer.Length];
        for (var i = 0; i < 6; i++)
            bts[i] = 0xFF;
        for (Int32 i = 6, k = 0; i < bts.Length; i++, k++)
        {
            if (k >= buffer.Length) k = 0;

            bts[i] = buffer[k];
        }

        var client = new UdpClient
        {
            EnableBroadcast = true
        };
        client.Send(bts, bts.Length, new IPEndPoint(IPAddress.Broadcast, 7));
        client.Close();
        //client.SendAsync(bts, bts.Length, new IPEndPoint(IPAddress.Broadcast, 7));
    }
    #endregion

    #region MAC获取/ARP协议
    [DllImport("Iphlpapi.dll")]
    private static extern Int32 SendARP(UInt32 destip, UInt32 srcip, Byte[] mac, ref Int32 length);

    /// <summary>根据IP地址获取MAC地址</summary>
    /// <param name="ip"></param>
    /// <returns></returns>
    public static Byte[] GetMac(this IPAddress ip)
    {
        // 考虑到IPv6是16字节，不确定SendARP是否支持IPv6
        var len = 16;
        var buf = new Byte[16];
        var rs = SendARP(ip.GetAddressBytes().ToUInt32(), 0, buf, ref len);
        if (rs != 0 || len <= 0) return null;

        if (len != buf.Length) buf = buf.ReadBytes(0, len);
        return buf;
    }
    #endregion

    #region IP地理位置
    /// <summary>IP地址提供者</summary>
    public static IIPResolver IpResolver { get; set; }

    /// <summary>获取IP地址的物理地址位置</summary>
    /// <param name="addr"></param>
    /// <returns></returns>
    public static String GetAddress(this IPAddress addr)
    {
        if (addr.IsAny()) return "任意地址";
        if (IPAddress.IsLoopback(addr)) return "本地环回";
        if (addr.IsLocal()) return "本机地址";

        //if (IpProvider == null) IpProvider = new MyIpProvider();

        return IpResolver?.GetAddress(addr);
    }

    /// <summary>根据字符串形式IP地址转为物理地址</summary>
    /// <param name="addr"></param>
    /// <returns></returns>
    public static String IPToAddress(this String addr)
    {
        if (addr.IsNullOrEmpty()) return String.Empty;

        // 有可能是NetUri
        var p = addr.IndexOf("://");
        if (p >= 0) addr = addr[(p + 3)..];

        // 有可能是多个IP地址
        p = addr.IndexOf(',');
        if (p >= 0) addr = addr.Split(',').FirstOrDefault();

        // 过滤IPv4/IPv6端口
        if (addr.Replace("::", "").Contains(':')) addr = addr[..addr.LastIndexOf(':')];

        return !IPAddress.TryParse(addr, out var ip) ? String.Empty : ip.GetAddress();
    }
    #endregion

    #region 创建客户端和会话
    /// <summary>根据本地网络标识创建客户端</summary>
    /// <param name="local"></param>
    /// <returns></returns>
    public static ISocketClient CreateClient(this NetUri local)
    {
        return local == null
            ? throw new ArgumentNullException(nameof(local))
            : local.Type switch
            {
                NetType.Tcp => new TcpSession { Local = local },
                NetType.Udp => new UdpServer { Local = local },
                _ => throw new NotSupportedException($"不支持{local.Type}协议"),
            };
    }

    /// <summary>根据远程网络标识创建客户端</summary>
    /// <param name="remote"></param>
    /// <returns></returns>
    public static ISocketClient CreateRemote(this NetUri remote)
    {
        return remote == null
            ? throw new ArgumentNullException(nameof(remote))
            : remote.Type switch
            {
                NetType.Tcp => new TcpSession { Remote = remote },
                NetType.Udp => new UdpServer { Remote = remote },
                NetType.Http => new TcpSession { Remote = remote, SslProtocol = remote.Port == 443 ? SslProtocols.Tls12 : SslProtocols.None },
                NetType.WebSocket => new TcpSession { Remote = remote, SslProtocol = remote.Port == 443 ? SslProtocols.Tls12 : SslProtocols.None },
                _ => throw new NotSupportedException($"不支持{remote.Type}协议"),
            };
    }

    internal static Socket CreateTcp(Boolean ipv4 = true) => new(ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);

    internal static Socket CreateUdp(Boolean ipv4 = true) => new(ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
    #endregion
}