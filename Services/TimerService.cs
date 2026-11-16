using System;
using System.Threading;
using GameModeHub.Core;
using GameModeHub.Views;

namespace GameModeHub.Services
{
    /// <summary>NOVO 4.2: "desligar/suspender em X minutos". Roda no PC, então funciona mesmo com o celular desligado.</summary>
    public static class TimerService
    {
        private static Timer _timer, _aviso;
        private static DateTime _quando;
        public static string Acao { get; private set; } = "";      // desligar | suspender | reiniciar | "" (nenhum)
        public static event Action Mudou;

        public static int SegundosRestantes => string.IsNullOrEmpty(Acao) ? 0 : Math.Max(0, (int)(_quando - DateTime.Now).TotalSeconds);

        public static bool Agendar(string acao, int minutos)
        {
            if (acao != "desligar" && acao != "suspender" && acao != "reiniciar") return false;
            minutos = Math.Clamp(minutos, 1, 24 * 60);
            Cancelar(false);
            Acao = acao;
            _quando = DateTime.Now.AddMinutes(minutos);
            _timer = new Timer(_ => Executar(), null, TimeSpan.FromMinutes(minutos), Timeout.InfiniteTimeSpan);
            if (minutos > 1) _aviso = new Timer(_ => ToastWindow.Mostrar(Texto(Acao) + " em 1 minuto", "Cancele pelo celular se quiser continuar.", "⏰"), null, TimeSpan.FromMinutes(minutos - 1), Timeout.InfiniteTimeSpan);
            Log.Info($"Timer: {acao} em {minutos} min");
            ToastWindow.Mostrar(Texto(acao) + $" em {minutos} min", "Agendado pelo celular.", "⏰");
            Mudou?.Invoke();
            return true;
        }

        public static void Cancelar(bool avisar = true)
        {
            _timer?.Dispose(); _aviso?.Dispose(); _timer = _aviso = null;
            if (avisar && !string.IsNullOrEmpty(Acao)) { ToastWindow.Mostrar("Timer cancelado", null, "⏰"); Log.Info("Timer cancelado"); }
            Acao = "";
            Mudou?.Invoke();
        }

        private static void Executar()
        {
            var a = Acao;
            Acao = ""; Mudou?.Invoke();
            Log.Info("Timer disparou: " + a);
            ConfigService.SalvarAgora();
            if (a == "desligar") PowerService.Desligar();
            else if (a == "reiniciar") PowerService.Reiniciar();
            else if (a == "suspender") PowerService.Suspender();
        }

        private static string Texto(string a) => a == "desligar" ? "Desligando" : a == "reiniciar" ? "Reiniciando" : "Suspendendo";
    }
}
