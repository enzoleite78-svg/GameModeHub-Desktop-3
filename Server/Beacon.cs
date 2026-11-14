using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameModeHub.Core;

namespace GameModeHub.Server
{
    /// <summary>Avisa na rede "estou aqui" para o celular achar o PC sozinho. Formato: GMH_SERVER|ip|porta|nome|mac</summary>
    public sealed class Beacon : IDisposable
    {
        private CancellationTokenSource _cts;
        private Task _task;

        public void Iniciar()
        {
            if (_task != null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _task = Task.Run(async () =>
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var a in NetInfo.Listar())
                        {
                            var msg = $"GMH_SERVER|{a.Ip}|{ConfigService.Atual.Porta}|{Environment.MachineName}|{a.Mac}";
                            var data = Encoding.UTF8.GetBytes(msg);
                            await udp.SendAsync(data, data.Length, new IPEndPoint(a.Broadcast, 5051));
                        }
                    }
                    catch (Exception ex) { Log.Warn("Beacon: " + ex.Message); }
                    try { await Task.Delay(2500, ct); } catch (OperationCanceledException) { break; }
                }
            });
        }

        public void Dispose() { try { _cts?.Cancel(); } catch { } _cts?.Dispose(); }
    }
}
