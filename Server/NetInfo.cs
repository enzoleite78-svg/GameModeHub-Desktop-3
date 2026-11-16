using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameModeHub.Server
{
    public static class NetInfo
    {
        public class Adaptador { public string Nome; public IPAddress Ip; public IPAddress Broadcast; public string Mac; public int Nota; }

        private static readonly string[] Virtuais =
            { "virtual", "hyper-v", "vmware", "virtualbox", "vbox", "tap-", "tun", "wireguard", "tailscale", "zerotier", "vpn", "loopback", "bluetooth", "wsl", "docker", "npcap", "hamachi" };

        private static IEnumerable<Adaptador> Todos()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }
                var desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                bool virtualNic = Virtuais.Any(desc.Contains);
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var ip = ua.Address.GetAddressBytes(); var mask = ua.IPv4Mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    int nota = 0;
                    if (gateway) nota += 100;
                    if (!virtualNic) nota += 50;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) nota += 10;
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) nota += 8;
                    if (ip[0] == 169 && ip[1] == 254) nota -= 200;            // APIPA: sem DHCP
                    if (!EhRedeLocal(ua.Address)) nota -= 20;
                    yield return new Adaptador { Nome = ni.Name, Ip = ua.Address, Broadcast = new IPAddress(bc), Mac = mac, Nota = nota };
                }
            }
        }

        public static List<Adaptador> Listar()
        {
            try { return Todos().ToList(); } catch { return new List<Adaptador>(); }
        }

        /// <summary>
        /// Melhor adaptador para o QR.
        /// CORREÇÃO 4.1: antes pegava o primeiro com gateway — às vezes era a VPN, o Hyper-V ou um IP 169.254 (sem rede),
        /// e o QR saía com um endereço que o celular não alcança.
        /// </summary>
        public static Adaptador Principal() => Listar().OrderByDescending(a => a.Nota).FirstOrDefault();

        /// <summary>Só aceita quem está na rede local (10.x, 172.16-31.x, 192.168.x, 169.254.x, loopback).</summary>
        public static bool EhRedeLocal(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily != AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
            var b = ip.GetAddressBytes();
            // NOVO 4.2: acesso de fora de casa pela VPN (Tailscale/ZeroTier usam 100.64.0.0/10), só se você ligar a opção
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return GameModeHub.Core.ConfigService.Atual.PermitirVpn;
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
    }
}
