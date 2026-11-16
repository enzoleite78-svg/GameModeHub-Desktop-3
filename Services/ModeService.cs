using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    public class ResultadoModo
    {
        public bool Ok { get; set; } = true;
        public List<string> Passos { get; } = new();
        public string Resumo => string.Join(" • ", Passos);
    }

    /// <summary>
    /// O que o modo mudou no PC, para o "Restaurar" desfazer exatamente isso.
    /// Fica salvo em disco (snapshot.json): se o PC/app reiniciar no meio de um modo, o Restaurar continua certo.
    /// </summary>
    public class EstadoModo
    {
        public List<string> Apps { get; set; } = new();
        public string ModoAtivoId { get; set; } = "";
        public string PlanoAnterior { get; set; } = "";
        public bool HdrLigadoPeloModo { get; set; }
        public int NotificacoesAnteriores { get; set; } = -1;
        public bool AbriuBigPicture { get; set; }
    }

    /// <summary>Orquestra o que acontece ao ativar um modo e ao restaurar.</summary>
    public static class ModeService
    {
        /// <summary>Id do layout de telas usado pelo Restaurar quando a opção é "Escolher quais telas acender".</summary>
        public const string IdLayoutRestaurar = "__restaurar";
        private static readonly SemaphoreSlim _trava = new SemaphoreSlim(1, 1);
        private static EstadoModo _estado = new EstadoModo();

        public static string ModoAtivoId { get; private set; } = "";
        public static bool Ocupado => _trava.CurrentCount == 0;
        public static event Action<string> ModoAtivoMudou;          // id ("" = desktop normal)
        public static event Action<string, string> Aviso;            // título, mensagem (vira toast)
        /// <summary>NOVO 4.1: texto do que está acontecendo ("Ativando…"), ou null quando termina. A janela mostra um indicador.</summary>
        public static event Action<string> Trabalhando;

        private static string SnapshotFile => Path.Combine(Paths.DataDir, "snapshot.json");

        /// <summary>Lê o estado salvo. CORREÇÃO 4.1: antes, ao reabrir o app no meio de um modo, ele dizia "Desktop normal".</summary>
        public static void Inicializar()
        {
            _estado = LerEstado();
            if (!string.IsNullOrEmpty(_estado.ModoAtivoId) && ConfigService.Perfil.Modos.Any(m => m.Id == _estado.ModoAtivoId))
                ModoAtivoId = _estado.ModoAtivoId;
        }

        public static ResultadoModo Ativar(string id)
        {
            var r = new ResultadoModo();
            var m = ConfigService.Perfil.Modos.FirstOrDefault(x => x.Id == id);
            if (m == null) { r.Ok = false; r.Passos.Add("Modo não encontrado"); return r; }
            if (!_trava.Wait(0)) { r.Ok = false; r.Passos.Add("Já estou trocando de modo, aguarde"); return r; }
            try
            {
                Trabalhando?.Invoke("Ativando “" + m.Nome + "”…");
                Log.Info("Ativando modo: " + m.Nome);
                var c = ConfigService.Atual;

                // 0) NOVO 4.3 (addon EmuSync): pega os saves mais novos da nuvem antes de abrir o jogo
                if (c.AddonEmuSync && c.EmuSyncAoAtivarModo)
                {
                    Trabalhando?.Invoke("Sincronizando saves antes de “" + m.Nome + "”…");
                    var s = EmuSyncAddon.SincronizarParaModo(true);
                    if (s != null) r.Passos.Add(s);
                    Trabalhando?.Invoke("Ativando “" + m.Nome + "”…");
                }

                // 1) fecha só o que o perfil manda, guardando o que foi fechado
                var perfil = ConfigService.Perfil;
                var manter = perfil.AppsFixados.Where(a => a.ManterAberto).Select(a => Path.GetFileNameWithoutExtension(a.Exe));
                var alvos = perfil.AppsMonitorados.Where(kv => kv.Value).Select(kv => kv.Key)
                    .Except(manter, StringComparer.OrdinalIgnoreCase).ToList();
                if (alvos.Count > 0)
                {
                    var fechados = ProcessService.Fechar(alvos, m.FecharGentil);
                    // se ativar dois modos seguidos, não perde a lista original
                    _estado.Apps = _estado.Apps.Union(fechados, StringComparer.OrdinalIgnoreCase).ToList();
                    if (fechados.Count > 0) r.Passos.Add($"{fechados.Count} app(s) fechado(s)");
                    SalvarEstado();
                }

                // 2) notificações do Windows (opcional, global)
                if (c.SilenciarNotificacoes && _estado.NotificacoesAnteriores < 0)
                {
                    _estado.NotificacoesAnteriores = SystemService.LerNotificacoes();
                    SystemService.DefinirNotificacoes(0);
                    r.Passos.Add("Notificações silenciadas");
                }

                // 3) telas
                var t = DisplayService.AplicarModo(m.Tela);
                if (m.Tela != "manter") r.Passos.Add(t.msg);
                if (!t.ok) r.Ok = false;

                // 4) HDR (só depois das telas, nas que ficaram acesas)
                if (m.Hdr)
                {
                    int n = DisplayService.DefinirHdrNasAtivas(true);
                    if (n == 0 && DisplayService.AlgumaAtivaSuportaHdr()) { Thread.Sleep(900); n = DisplayService.DefinirHdrNasAtivas(true); }
                    if (n > 0) _estado.HdrLigadoPeloModo = true;
                    r.Passos.Add(n > 0 ? "HDR ligado" : "HDR: a tela não suporta");
                }

                // 5) áudio (nunca sobrescreve config se o dispositivo não for achado)
                if (m.UsarSaida == "A" || m.UsarSaida == "B")
                {
                    var nome = AudioService.AplicarSlot(m.UsarSaida);
                    r.Passos.Add(nome != null ? "Áudio: " + nome : "Áudio: saída " + m.UsarSaida + " não encontrada");
                    if (nome == null) r.Ok = false;
                }

                // 6) energia (guarda o plano de antes para voltar exatamente a ele)
                if (!string.IsNullOrEmpty(m.PlanoEnergia))
                {
                    if (string.IsNullOrEmpty(_estado.PlanoAnterior)) _estado.PlanoAnterior = PowerService.PlanoAtual();
                    bool ok = PowerService.DefinirPlano(m.PlanoEnergia);
                    r.Passos.Add(ok ? "Energia: " + (m.PlanoEnergia == "alto" ? "alto desempenho" : "equilibrado") : "Energia: plano indisponível neste PC");
                }

                // 7) memória
                if (m.LiberarRam)
                {
                    long livre = ProcessService.LiberarMemoria();
                    r.Passos.Add(livre > 64L * 1048576 ? $"{livre / 1048576} MB de RAM liberados" : "RAM otimizada");
                }

                // 8) launcher
                if (m.Launcher != "Nenhum")
                {
                    Thread.Sleep(1200); // deixa a tela estabilizar antes de abrir
                    bool ok = LauncherService.Abrir(m);
                    if (ok && m.Launcher == "Steam Big Picture") _estado.AbriuBigPicture = true;
                    r.Passos.Add(ok ? "Abrindo " + LauncherService.Descricao(m) : LauncherService.Descricao(m) + " não encontrado");
                }

                // 9) cursor fora do caminho
                if (m.EsconderCursor) InputService.EsconderCursor();

                ModoAtivoId = m.Id;
                _estado.ModoAtivoId = m.Id;
                SalvarEstado();
                ModoAtivoMudou?.Invoke(m.Id);
                Aviso?.Invoke(m.Nome, r.Resumo);
                return r;
            }
            catch (Exception ex)
            {
                Log.Error("Ativar modo", ex);
                r.Ok = false; r.Passos.Add("Erro: " + ex.Message); return r;
            }
            finally { _trava.Release(); Trabalhando?.Invoke(null); }
        }

        /// <summary>Volta ao desktop normal e desfaz o que o modo mudou.</summary>
        public static ResultadoModo Restaurar()
        {
            var r = new ResultadoModo();
            if (!_trava.Wait(0)) { r.Ok = false; r.Passos.Add("Já estou trocando de modo, aguarde"); return r; }
            try
            {
                Trabalhando?.Invoke("Restaurando o desktop…");
                Log.Info("Restaurando desktop");
                var c = ConfigService.Atual;
                var modoAntes = ConfigService.Perfil.Modos.FirstOrDefault(x => x.Id == ModoAtivoId);

                // 1) telas (configurável em Geral → "Ao restaurar")
                var t = TelasAoRestaurar(c.RestaurarTelas);
                if (!string.IsNullOrEmpty(t.msg)) r.Passos.Add(t.msg);
                if (!t.ok) r.Ok = false;

                // 2) HDR: CORREÇÃO 4.1 — só desliga se foi o modo que ligou (antes desligava o HDR de quem usa HDR no dia a dia)
                if (_estado.HdrLigadoPeloModo) DisplayService.DefinirHdrNasAtivas(false);

                // 3) áudio
                if (c.RestaurarAudio == "A" || c.RestaurarAudio == "B")
                {
                    var nome = AudioService.AplicarSlot(c.RestaurarAudio);
                    if (nome != null) r.Passos.Add("Áudio: " + nome);
                }

                // 4) energia: CORREÇÃO 4.1 — volta para o plano que estava antes (antes forçava sempre "Equilibrado")
                if (!string.IsNullOrEmpty(_estado.PlanoAnterior)) PowerService.DefinirPlano(_estado.PlanoAnterior);
                else if (modoAntes != null && modoAntes.PlanoEnergia == "alto") PowerService.DefinirPlano("equilibrado");

                // 5) notificações
                if (_estado.NotificacoesAnteriores >= 0) SystemService.DefinirNotificacoes(_estado.NotificacoesAnteriores);

                // 6) Big Picture: fecha a interface de TV da Steam (comportamento da v3)
                if (_estado.AbriuBigPicture) ProcessService.Abrir("steam://close/bigpicture");

                // 7) reabre exatamente o que foi fechado (sem abrir duplicado o que o usuário já reabriu)
                int reabertos = 0;
                foreach (var a in _estado.Apps)
                    if (!ProcessService.EstaRodando(a)) { ProcessService.Abrir(a); reabertos++; }
                if (reabertos > 0) r.Passos.Add($"{reabertos} app(s) reaberto(s)");

                InputService.LiberarCursor();

                // 8) NOVO 4.3 (addon EmuSync): manda para a nuvem o save de quem acabou de jogar
                if (c.AddonEmuSync && c.EmuSyncAoRestaurar)
                {
                    Trabalhando?.Invoke("Sincronizando saves…");
                    var s = EmuSyncAddon.SincronizarParaModo(false);
                    if (s != null) r.Passos.Add(s);
                }

                _estado = new EstadoModo();
                try { if (File.Exists(SnapshotFile)) File.Delete(SnapshotFile); } catch { }

                ModoAtivoId = "";
                ModoAtivoMudou?.Invoke("");
                Aviso?.Invoke("Desktop normal", r.Resumo);
                return r;
            }
            catch (Exception ex) { Log.Error("Restaurar", ex); r.Ok = false; r.Passos.Add("Erro: " + ex.Message); return r; }
            finally { _trava.Release(); Trabalhando?.Invoke(null); }
        }

        /// <summary>auto | todas | manter | modo:&lt;id&gt;</summary>
        private static (bool ok, string msg) TelasAoRestaurar(string opcao)
        {
            opcao ??= "auto";
            if (opcao == "manter") return (true, null);
            if (opcao == "escolher")
            {
                // NOVO 4.1.1: telas escolhidas uma a uma em Geral → Ao restaurar o desktop
                var lay = ConfigService.Layouts.FirstOrDefault(l => l.Id == IdLayoutRestaurar);
                if (lay == null || lay.TelasAtivas.Count == 0) return DisplayService.AcenderTodas();
                var r = DisplayService.AplicarLayout(lay);
                return r.ok ? r : DisplayService.AcenderTodas();
            }
            if (opcao == "todas") return DisplayService.AcenderTodas();
            if (opcao.StartsWith("modo:"))
            {
                var m = ConfigService.Perfil.Modos.FirstOrDefault(x => x.Id == opcao.Substring(5));
                if (m != null && m.Tela != "manter")
                {
                    var r = DisplayService.AplicarModo(m.Tela);
                    return r.ok ? r : DisplayService.AcenderTodas();
                }
                return DisplayService.AcenderTodas();
            }
            // auto: usa as telas do modo "Jogar no Monitor" (id "pc") se ele ainda usa telas escolhidas; senão acende tudo.
            // CORREÇÃO 4.1: antes usava um layout "pc" velho mesmo depois de você mudar/excluir esse modo.
            var pc = ConfigService.Layouts.FirstOrDefault(l => l.Id == "pc");
            bool usado = ConfigService.Perfil.Modos.Any(x => x.Tela == "layout:pc");
            if (pc != null && usado && pc.TelasAtivas.Count > 0)
            {
                var r = DisplayService.AplicarLayout(pc);
                return r.ok ? r : DisplayService.AcenderTodas();
            }
            return DisplayService.AcenderTodas();
        }

        private static EstadoModo LerEstado()
        {
            try
            {
                if (!File.Exists(SnapshotFile)) return new EstadoModo();
                var txt = File.ReadAllText(SnapshotFile).TrimStart();
                // formato da 4.0: só a lista de apps
                if (txt.StartsWith("[")) return new EstadoModo { Apps = JsonSerializer.Deserialize<List<string>>(txt) ?? new() };
                var e = JsonSerializer.Deserialize<EstadoModo>(txt) ?? new EstadoModo();
                e.Apps ??= new(); e.ModoAtivoId ??= ""; e.PlanoAnterior ??= "";
                return e;
            }
            catch { return new EstadoModo(); }
        }

        private static void SalvarEstado()
        {
            try { File.WriteAllText(SnapshotFile, JsonSerializer.Serialize(_estado)); } catch { }
        }
    }
}
