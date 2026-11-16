using GameModeHub.Core;

namespace GameModeHub.Services
{
    /// <summary>CPU e RAM sem PerformanceCounter (leve). Chame só quando o painel estiver visível.</summary>
    public static class StatsService
    {
        private static readonly object _lock = new object();
        private static long _idle, _kernel, _user;
        private static bool _tem;
        private static int _ultimaCpu;
        private static System.DateTime _ultimaLeitura = System.DateTime.MinValue;

        public static (int cpu, int ramPct, double ramUsadaGb, double ramTotalGb) Ler()
        {
            int cpu;
            // CORREÇÃO 4.1: janela e celular liam ao mesmo tempo e "roubavam" a amostra um do outro (CPU pulava para 0 ou 100)
            lock (_lock)
            {
                cpu = _ultimaCpu;
                if ((System.DateTime.UtcNow - _ultimaLeitura).TotalMilliseconds >= 900 && Native.GetSystemTimes(out long i, out long k, out long u))
                {
                    if (_tem)
                    {
                        long di = i - _idle, dk = k - _kernel, du = u - _user;
                        long total = dk + du;
                        if (total > 0) cpu = (int)((total - di) * 100 / total);
                    }
                    _idle = i; _kernel = k; _user = u; _tem = true;
                    _ultimaCpu = cpu = System.Math.Clamp(cpu, 0, 100);
                    _ultimaLeitura = System.DateTime.UtcNow;
                }
            }
            var m = new Native.MEMORYSTATUSEX();
            Native.GlobalMemoryStatusEx(m);
            double tot = m.ullTotalPhys / 1073741824.0;
            double usada = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            return (cpu, (int)m.dwMemoryLoad, usada, tot);
        }

        public static long RamLivreBytes()
        {
            var m = new Native.MEMORYSTATUSEX();
            return Native.GlobalMemoryStatusEx(m) ? (long)m.ullAvailPhys : 0;
        }
    }
}
