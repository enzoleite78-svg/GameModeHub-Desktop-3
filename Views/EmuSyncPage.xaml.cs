using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameModeHub.Core;
using GameModeHub.Services;

namespace GameModeHub.Views
{
    /// <summary>NOVO 4.3: tela do addon EmuSync (jogos, sync, backups, aparelhos, nuvem).</summary>
    public partial class EmuSyncPage : UserControl
    {
        private EsAparelho _local;
        private List<EsAparelho> _aparelhos = new();
        private List<EsJogo> _jogos = new();
        private string _aba = "jogos";
        private bool _carregando;
        private bool _ativa;
        private int _geracao;
        private DateTime _proximoAutoUtc = DateTime.MinValue;
        private DateTime _ultimaCarga = DateTime.MinValue;

        // editor
        private EsJogo _editando;              // null = jogo novo
        private readonly Dictionary<string, TextBox> _camposPasta = new();

        // jogos com sync em andamento nesta tela (id → texto do botão)
        private readonly HashSet<string> _sincronizando = new();

        private readonly DispatcherTimer _relogio = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        /// <summary>Pede para a janela principal abrir Geral → Addons.</summary>
        public event Action IrParaAddons;

        public EmuSyncPage()
        {
            InitializeComponent();
            _relogio.Tick += (s, e) => Tique();
            PreviewKeyDown += AoTeclar;
            EmuSyncAddon.SyncTerminou += () => Dispatcher.BeginInvoke(new Action(() => { if (_ativa && !_carregando) _ = Carregar(false); }));
        }

        // ================= CICLO DE VIDA =================
        /// <summary>Chamado pela janela quando a aba fica visível.</summary>
        public void Mostrar()
        {
            _ativa = true;
            _relogio.Start();
            if (PainelEditor.Visibility == Visibility.Visible || PainelBackups.Visibility == Visibility.Visible) return;
            _ = Carregar(true);
        }

        public void Esconder()
        {
            _ativa = false;
            _relogio.Stop();
        }

        private void Tique()
        {
            if (!_ativa) return;
            if (_proximoAutoUtc > DateTime.MinValue)
            {
                var s = (_proximoAutoUtc - DateTime.UtcNow).TotalSeconds;
                TxtProximo.Text = s > 1 ? "Próximo sync automático do EmuSync em " + Duracao(s) : "Sync automático do EmuSync rodando agora…";
                // passou da hora: pergunta de novo ao EmuSync quando será o próximo (a cada 5 s)
                if (s < -2 && (DateTime.UtcNow - _ultimaConsultaAuto).TotalSeconds > 5) _ = AtualizarProximoAuto();
            }
            // recarrega sozinho de vez em quando (o sync automático do EmuSync muda os estados)
            if (!_carregando && _sincronizando.Count == 0 && PainelJogos.Visibility == Visibility.Visible
                && (DateTime.UtcNow - _ultimaCarga).TotalSeconds > 45)
                _ = Carregar(false);
        }

        private DateTime _ultimaConsultaAuto = DateTime.MinValue;

        private async Task AtualizarProximoAuto()
        {
            _ultimaConsultaAuto = DateTime.UtcNow;
            try
            {
                var s = await EmuSyncClient.SegundosParaProximoAuto();
                if (s > 0)
                {
                    _proximoAutoUtc = DateTime.UtcNow.AddSeconds(s);
                    if (!_carregando && _sincronizando.Count == 0) _ = Carregar(false);   // o sync automático pode ter mudado os estados
                }
            }
            catch { }
        }

        private void AoTeclar(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5) { _ = Carregar(true); e.Handled = true; }
            else if (e.Key == Key.Escape && (PainelEditor.Visibility == Visibility.Visible || PainelBackups.Visibility == Visibility.Visible)) { VoltarParaLista(); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && PainelEditor.Visibility == Visibility.Visible) { BtnSalvarJogo_Click(null, null); e.Handled = true; }
        }

        // ================= CARREGAR =================
        private async Task Carregar(bool mostrarIndicador)
        {
            if (_carregando) return;
            _carregando = true;
            int geracao = ++_geracao;
            if (mostrarIndicador && _jogos.Count == 0) Trabalhando("Conversando com o EmuSync…");
            try
            {
                if (!await EmuSyncClient.Online())
                {
                    MostrarOffline(true);
                    return;
                }
                MostrarOffline(false);

                var tLocal = EmuSyncClient.Local();
                var tAparelhos = EmuSyncClient.Aparelhos();
                await Task.WhenAll(tLocal, tAparelhos);
                _local = tLocal.Result;
                _aparelhos = tAparelhos.Result;
                bool conectado = _local.Provedor != EsProvedor.Nenhum;

                if (conectado)
                {
                    var tJogos = EmuSyncClient.Jogos();
                    var tProx = SegurarErro(EmuSyncClient.SegundosParaProximoAuto(), -1);
                    await Task.WhenAll(tJogos, tProx);
                    _jogos = tJogos.Result;
                    _proximoAutoUtc = tProx.Result >= 0 ? DateTime.UtcNow.AddSeconds(tProx.Result) : DateTime.MinValue;
                }
                else { _jogos = new(); _proximoAutoUtc = DateTime.MinValue; }

                if (geracao != _geracao) return;
                _ultimaCarga = DateTime.UtcNow;
                RenderizarEstado(conectado);
                RenderizarAba();
            }
            catch (EmuSyncException ex) { if (geracao == _geracao) MostrarErro("Não consegui ler o EmuSync", ex.Message); }
            catch (Exception ex) { Log.Error("EmuSync: carregar", ex); if (geracao == _geracao) MostrarErro("Não consegui ler o EmuSync", ex.Message); }
            finally
            {
                _carregando = false;
                if (mostrarIndicador) Trabalhando(null);
            }
        }

        private static async Task<T> SegurarErro<T>(Task<T> t, T padrao)
        {
            try { return await t; } catch { return padrao; }
        }

        private void MostrarOffline(bool offline)
        {
            CardOffline.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
            Conteudo.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
            if (offline) _ = AjustarTextoOffline();
        }

        private async Task AjustarTextoOffline()
        {
            var d = await EmuSyncAddon.Detectar();
            if (d.ApiOnline) { _ = Carregar(false); return; }
            TxtOffline.Text = d.ServicoInstalado
                ? "O serviço de sincronização do EmuSync (EmuSyncAgent) está parado. Ligue o serviço abaixo; o Windows vai pedir permissão."
                : "O EmuSync não está instalado neste PC (ou foi desinstalado). Instale de novo em Geral → Addons.";
            BtnLigarServico.Visibility = d.ServicoInstalado ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RenderizarEstado(bool conectado)
        {
            CardSemNuvem.Visibility = conectado ? Visibility.Collapsed : Visibility.Visible;
            BtnSyncTudo.IsEnabled = conectado && !EmuSyncClient.SincronizandoTudo;
            BtnNovoJogo.IsEnabled = conectado;
            AbaJogos.Content = "🎮  Jogos" + (conectado ? $" ({_jogos.Count})" : "");
            AbaAparelhos.Content = "💻  Aparelhos" + (_aparelhos.Count > 0 ? $" ({_aparelhos.Count})" : "");

            int pendentes = _jogos.Count(j => j.PastaDe(_local?.Id) != null && (j.Status == EsStatus.PrecisaBaixar || j.Status == EsStatus.PrecisaEnviar));
            Brush cor = !conectado ? Recurso("MutedBrush") : pendentes > 0 ? new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)) : Recurso("AccentBrush");
            PontoNuvem.Fill = cor; HaloNuvem.Fill = cor;
            TxtNuvem.Text = !conectado ? $"“{_local?.Nome}” ainda sem nuvem"
                : $"“{_local.Nome}” · {NomeProvedor(_local.Provedor)}" + (pendentes > 0 ? $" · {pendentes} para sincronizar" : " · tudo em dia");
            if (_proximoAutoUtc == DateTime.MinValue) TxtProximo.Text = conectado ? "" : "Conecte uma nuvem para começar.";
            else Tique();
        }

        private void RenderizarAba()
        {
            switch (_aba)
            {
                case "jogos": RenderizarJogos(); break;
                case "aparelhos": RenderizarAparelhos(); break;
                case "historico": _ = RenderizarHistorico(); break;
                case "configurar": RenderizarConfigurar(); break;
            }
        }

        // ================= ABAS =================
        private void Aba_Click(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _aba = (string)((FrameworkElement)sender).Tag;
            MostrarPainel(_aba);
            RenderizarAba();
        }

        private void MostrarPainel(string nome)
        {
            PainelJogos.Visibility = nome == "jogos" ? Visibility.Visible : Visibility.Collapsed;
            PainelAparelhos.Visibility = nome == "aparelhos" ? Visibility.Visible : Visibility.Collapsed;
            PainelHistorico.Visibility = nome == "historico" ? Visibility.Visible : Visibility.Collapsed;
            PainelConfigurar.Visibility = nome == "configurar" ? Visibility.Visible : Visibility.Collapsed;
            PainelEditor.Visibility = nome == "editor" ? Visibility.Visible : Visibility.Collapsed;
            PainelBackups.Visibility = nome == "backups" ? Visibility.Visible : Visibility.Collapsed;
            Abas.Visibility = nome == "editor" || nome == "backups" ? Visibility.Collapsed : Visibility.Visible;
            Rolagem.ScrollToTop();
        }

        private void VoltarParaLista()
        {
            MostrarPainel(_aba);
            _ = Carregar(false);
        }

        private void BtnVoltarLista_Click(object sender, RoutedEventArgs e) => VoltarParaLista();

        private void BtnIrConfigurar_Click(object sender, RoutedEventArgs e)
        {
            _aba = "configurar"; AbaConfigurar.IsChecked = true;
            MostrarPainel(_aba); RenderizarAba();
        }

        private void BtnIrAddons_Click(object sender, RoutedEventArgs e) => IrParaAddons?.Invoke();
        private void BtnAtualizar_Click(object sender, RoutedEventArgs e) => _ = Carregar(true);

        private async void BtnAbrirOriginal_Click(object sender, RoutedEventArgs e) => EmuSyncAddon.AbrirAppOriginal(await EmuSyncAddon.Detectar());

        private async void BtnLigarServico_Click(object sender, RoutedEventArgs e)
        {
            BtnLigarServico.IsEnabled = false;
            Trabalhando("Ligando o serviço do EmuSync…");
            bool ok = await EmuSyncAddon.IniciarServico();
            Trabalhando(null);
            BtnLigarServico.IsEnabled = true;
            ToastWindow.Mostrar(ok ? "EmuSync ligado" : "O serviço não ligou", ok ? null : "Clique em “Sim” no aviso do Windows, ou abra o EmuSync original uma vez.", ok ? "✔" : "⚠");
            if (ok) _ = Carregar(true);
        }

        // ================= JOGOS =================
        private void Filtro_Click(object sender, RoutedEventArgs e) { if (IsLoaded) RenderizarJogos(); }
        private void EdBusca_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) RenderizarJogos(); }

        private void RenderizarJogos()
        {
            ListaJogos.Children.Clear();
            if (_local == null) return;
            if (_local.Provedor == EsProvedor.Nenhum) { TxtContagem.Text = ""; return; }

            var busca = (EdBusca.Text ?? "").Trim();
            IEnumerable<EsJogo> lista = _jogos;
            if (busca.Length > 0) lista = lista.Where(j => j.Nome.Contains(busca, StringComparison.CurrentCultureIgnoreCase));
            if (FiltroPendentes.IsChecked == true) lista = lista.Where(j => j.PastaDe(_local.Id) != null && (j.Status == EsStatus.PrecisaBaixar || j.Status == EsStatus.PrecisaEnviar));
            if (FiltroNestePc.IsChecked == true) lista = lista.Where(j => j.PastaDe(_local.Id) != null);
            var itens = lista.OrderBy(j => j.PastaDe(_local.Id) == null).ThenBy(j => j.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();

            int comPasta = _jogos.Count(j => j.PastaDe(_local.Id) != null);
            TxtContagem.Text = $"{_jogos.Count} jogo(s) na nuvem · {comPasta} com pasta neste PC" + (itens.Count != _jogos.Count ? $" · mostrando {itens.Count}" : "");

            if (_jogos.Count == 0)
            {
                ListaJogos.Children.Add(CartaoVazio("Nenhum jogo ainda", "Clique em “＋ Adicionar jogo” e escolha a pasta de saves do emulador ou do jogo."));
                return;
            }
            if (itens.Count == 0) { ListaJogos.Children.Add(new TextBlock { Text = "Nenhum jogo com esse filtro.", Style = Estilo("Sub"), Margin = new Thickness(2, 6, 0, 0) }); return; }
            foreach (var j in itens) ListaJogos.Children.Add(CartaoJogo(j));
        }

        private Border CartaoJogo(EsJogo j)
        {
            var pasta = j.PastaDe(_local.Id);
            bool temPasta = !string.IsNullOrWhiteSpace(pasta);
            var (rotulo, cor) = DescreverStatus(temPasta ? j.Status : EsStatus.SemPasta);

            var cartao = new Border { Style = Estilo("Card"), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(18, 14, 18, 14) };
            var dock = new DockPanel();

            // ações (direita)
            var acoes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(acoes, Dock.Right);
            var auto = new CheckBox { Style = Estilo("Interruptor"), Content = "Auto", IsChecked = j.AutoSync, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
                                      ToolTip = "Sync automático do EmuSync para este jogo (vale para todos os aparelhos)" };
            auto.Click += async (s, e) => await AlternarAuto(j, auto);
            acoes.Children.Add(auto);

            bool ocupado = _sincronizando.Contains(j.Id);
            var sync = new Button
            {
                Style = Estilo(temPasta && (j.Status == EsStatus.PrecisaBaixar || j.Status == EsStatus.PrecisaEnviar) ? "BtnPrimario" : "BtnSecundario"),
                Content = ocupado ? "Sincronizando…" : "🔄  Sincronizar",
                IsEnabled = temPasta && !ocupado,
                Padding = new Thickness(14, 8, 14, 8),
                ToolTip = temPasta ? "Envia ou baixa conforme o save mais novo" : "Escolha a pasta deste PC em ✎ Editar"
            };
            sync.Click += async (s, e) => await SincronizarJogo(j, sync, "sync");
            acoes.Children.Add(sync);

            var mais = new Button { Style = Estilo("BtnIcone"), Content = "⋯", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Mais opções" };
            mais.Click += (s, e) => AbrirMenuJogo(j, mais, sync, temPasta);
            acoes.Children.Add(mais);
            dock.Children.Add(acoes);

            // ponto de estado
            var ponto = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 14, 0) };
            ponto.Children.Add(new System.Windows.Shapes.Ellipse { Fill = cor, Opacity = 0.25, Width = 12, Height = 12 });
            ponto.Children.Add(new System.Windows.Shapes.Ellipse { Fill = cor, Width = 7, Height = 7 });
            DockPanel.SetDock(ponto, Dock.Left);
            dock.Children.Add(ponto);

            // textos
            var txt = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var titulo = new WrapPanel();
            titulo.Children.Add(new TextBlock { Text = j.Nome, FontSize = 15.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            titulo.Children.Add(Chip(rotulo, cor));
            txt.Children.Add(titulo);
            txt.Children.Add(new TextBlock
            {
                Text = temPasta ? "📁  " + pasta : "Sem pasta neste PC — use ✎ Editar para escolher",
                Style = Estilo("Sub"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), ToolTip = temPasta ? pasta : null
            });
            var partes = new List<string>();
            if (j.UltimoSyncUtc is DateTime quando) partes.Add("Último sync " + Relativo(quando) + (j.UltimoSyncDe != null ? " · de " + NomeAparelho(j.UltimoSyncDe) : ""));
            if (j.Bytes is long b && b > 0) partes.Add(Tamanho(b));
            int outros = j.Pastas?.Count(kv => kv.Key != _local.Id && !string.IsNullOrWhiteSpace(kv.Value)) ?? 0;
            if (outros > 0) partes.Add($"em mais {outros} aparelho(s)");
            if (partes.Count > 0) txt.Children.Add(new TextBlock { Text = string.Join("  ·  ", partes), Style = Estilo("Sub"), Margin = new Thickness(0, 2, 0, 0) });
            dock.Children.Add(txt);

            cartao.Child = dock;
            return cartao;
        }

        private void AbrirMenuJogo(EsJogo j, Button origem, Button sync, bool temPasta)
        {
            var menu = new ContextMenu { PlacementTarget = origem, Placement = PlacementMode.Bottom };
            void Item(string texto, bool ativo, Action acao) { var mi = new MenuItem { Header = texto, IsEnabled = ativo }; mi.Click += (s, e) => acao(); menu.Items.Add(mi); }
            Item("✎   Editar (nome, pastas, opções)", true, () => AbrirEditor(j));
            Item("🗂   Backups deste PC", temPasta, () => _ = AbrirBackups(j));
            Item("📂   Abrir a pasta", temPasta && Directory.Exists(j.PastaDe(_local.Id)), () => ProcessService.Abrir(j.PastaDe(_local.Id)));
            menu.Items.Add(new Separator());
            Item("⬆   Forçar envio (este PC → nuvem)", temPasta, () => _ = ConfirmarForcar(j, sync, true));
            Item("⬇   Forçar download (nuvem → este PC)", temPasta, () => _ = ConfirmarForcar(j, sync, false));
            menu.Items.Add(new Separator());
            Item("🗑   Excluir jogo", true, () => _ = ExcluirJogo(j));
            menu.IsOpen = true;
        }

        private async Task AlternarAuto(EsJogo j, CheckBox chk)
        {
            bool novo = chk.IsChecked == true;
            chk.IsEnabled = false;
            try
            {
                await EmuSyncClient.SalvarJogo(j.Id, j.Nome, novo, j.Pastas ?? new(), j.MaxBackups);
                j.AutoSync = novo;
                ToastWindow.Mostrar(novo ? "Sync automático ligado" : "Sync automático desligado", j.Nome, "🔄");
            }
            catch (EmuSyncException ex) { chk.IsChecked = !novo; MostrarErro("Não consegui mudar", ex.Message); }
            finally { chk.IsEnabled = true; }
        }

        private async Task ConfirmarForcar(EsJogo j, Button sync, bool envio)
        {
            var msg = envio
                ? $"Mandar o save DESTE PC de “{j.Nome}” para a nuvem, mesmo que o da nuvem seja mais novo?\n\nO save que está na nuvem é substituído. Os outros aparelhos vão baixar este."
                : $"Baixar o save da NUVEM de “{j.Nome}” para este PC, mesmo que o deste PC seja mais novo?\n\nO EmuSync guarda um backup do save atual deste PC antes (se os backups estiverem ligados).";
            if (MessageBox.Show(Window.GetWindow(this), msg, "EmuSync", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await SincronizarJogo(j, sync, envio ? "enviar" : "baixar");
        }

        private async Task SincronizarJogo(EsJogo j, Button botao, string tipo)
        {
            if (!_sincronizando.Add(j.Id)) return;
            botao.IsEnabled = false;
            var original = botao.Content;
            botao.Content = "Sincronizando…";
            var acompanhar = AcompanharProgresso(j.Id, botao);
            try
            {
                string feito;
                if (tipo == "enviar") { await EmuSyncClient.ForcarEnvio(j.Id); feito = "Enviado para a nuvem"; }
                else if (tipo == "baixar") { await EmuSyncClient.ForcarDownload(j.Id); feito = "Baixado da nuvem"; }
                else
                {
                    var st = await EmuSyncClient.StatusJogo(j.Id);
                    if (!st.PrecisaEnviar && !st.PrecisaBaixar) feito = "Já estava em dia";
                    else { await EmuSyncClient.Sincronizar(j.Id); feito = st.PrecisaEnviar ? "Enviado para a nuvem" : "Baixado da nuvem"; }
                }
                ToastWindow.Mostrar(j.Nome, feito, "✔");
            }
            catch (EmuSyncException ex) { MostrarErro("Não consegui sincronizar “" + j.Nome + "”", ex.Message); botao.Content = original; }
            finally
            {
                _sincronizando.Remove(j.Id);
                await acompanhar;
                await Carregar(false);
            }
        }

        /// <summary>Mostra a % do EmuSync no botão enquanto o sync acontece.</summary>
        private async Task AcompanharProgresso(string id, Button botao)
        {
            await Task.Delay(400);
            while (_sincronizando.Contains(id))
            {
                try
                {
                    var p = await EmuSyncClient.Progresso(id);
                    if (p.EmAndamento && _sincronizando.Contains(id))
                        botao.Content = p.Percentual is double pc ? $"Sincronizando… {pc:0}%" : "Sincronizando…";
                }
                catch { }
                await Task.Delay(700);
            }
        }

        private async void BtnSyncTudo_Click(object sender, RoutedEventArgs e)
        {
            BtnSyncTudo.IsEnabled = false;
            Trabalhando("Sincronizando todos os jogos…");
            try
            {
                var r = await EmuSyncClient.SincronizarTudo(t => Dispatcher.BeginInvoke(new Action(() => Trabalhando(t))));
                ToastWindow.Mostrar(r.Falhas > 0 ? "Alguns jogos deram erro" : "Saves sincronizados", r.Falhas > 0 ? r.Resumo + " — " + r.Erros.First() : r.Resumo, r.Falhas > 0 ? "⚠" : "✔");
            }
            catch (EmuSyncException ex) { MostrarErro("Não consegui sincronizar", ex.Message); }
            finally { Trabalhando(null); BtnSyncTudo.IsEnabled = true; await Carregar(false); }
        }

        private async Task ExcluirJogo(EsJogo j)
        {
            var msg = $"Excluir “{j.Nome}” do EmuSync?\n\nO jogo some da lista em TODOS os aparelhos (PC, Steam Deck, celular) e o save guardado na nuvem é apagado.\nOs saves que estão nas pastas de cada aparelho NÃO são apagados.";
            if (MessageBox.Show(Window.GetWindow(this), msg, "EmuSync", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Trabalhando("Excluindo " + j.Nome + "…");
            try
            {
                await EmuSyncClient.ExcluirJogo(j.Id);
                ToastWindow.Mostrar("Jogo excluído", j.Nome, "🗑");
                if (PainelEditor.Visibility == Visibility.Visible) MostrarPainel(_aba);
            }
            catch (EmuSyncException ex) { MostrarErro("Não consegui excluir", ex.Message); }
            finally { Trabalhando(null); await Carregar(false); }
        }

        // ================= EDITOR =================
        private void BtnNovoJogo_Click(object sender, RoutedEventArgs e) => AbrirEditor(null);

        private void AbrirEditor(EsJogo j)
        {
            _editando = j;
            TxtTituloEditor.Text = j == null ? "Adicionar jogo" : "Editar “" + j.Nome + "”";
            EdNomeJogo.Text = j?.Nome ?? "";
            EdAutoSync.IsChecked = j?.AutoSync ?? true;
            EdMaxBackups.Text = j?.MaxBackups?.ToString() ?? "";
            BtnExcluirJogo.Visibility = j == null ? Visibility.Collapsed : Visibility.Visible;
            BlocoSugestoes.Visibility = j == null ? Visibility.Visible : Visibility.Collapsed;

            // uma linha por aparelho (este PC primeiro)
            PainelPastas.Children.Clear(); _camposPasta.Clear();
            var ordem = _aparelhos.OrderBy(a => a.Id != _local.Id).ThenBy(a => a.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var a in ordem)
            {
                bool esteMesmo = a.Id == _local.Id;
                var linha = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var rotulo = new StackPanel { Width = 220, VerticalAlignment = VerticalAlignment.Center };
                rotulo.Children.Add(new TextBlock { Text = a.Icone + "  " + a.Nome, FontWeight = esteMesmo ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = a.Nome });
                rotulo.Children.Add(new TextBlock { Text = esteMesmo ? "este PC" : a.Plataforma, Style = Estilo("Sub"), FontSize = 11.5 });
                DockPanel.SetDock(rotulo, Dock.Left);
                linha.Children.Add(rotulo);

                var campo = new TextBox { Text = j?.PastaDe(a.Id) ?? "", Tag = esteMesmo ? "Escolha a pasta de saves neste PC" : "vazio = esse aparelho não tem o jogo" };
                if (esteMesmo)
                {
                    var procurar = new Button { Style = Estilo("BtnSecundario"), Content = "📂  Procurar…", Margin = new Thickness(8, 0, 0, 0) };
                    procurar.Click += (s, e) => EscolherPasta(campo);
                    DockPanel.SetDock(procurar, Dock.Right);
                    linha.Children.Add(procurar);
                }
                linha.Children.Add(campo);
                _camposPasta[a.Id] = campo;
                PainelPastas.Children.Add(linha);
            }

            MostrarPainel("editor");
            if (j == null) _ = CarregarSugestoes();
            EdNomeJogo.Focus();
        }

        private async Task CarregarSugestoes()
        {
            CmbSugestoes.ItemsSource = null;
            TxtSugestoes.Text = "Procurando sugestões…";
            try
            {
                var lista = await EmuSyncClient.Sugestoes();
                // esconde o que já está na lista do EmuSync
                var existentes = new HashSet<string>(_jogos.Select(x => x.Nome), StringComparer.CurrentCultureIgnoreCase);
                lista = lista.Where(s => !existentes.Contains(s.Nome) && s.Pastas?.Count > 0).OrderBy(s => s.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();
                CmbSugestoes.ItemsSource = lista;
                TxtSugestoes.Text = lista.Count > 0
                    ? $"{lista.Count} jogo(s) de PC encontrados com saves neste PC. Escolha um para preencher nome e pasta, ou preencha à mão."
                    : "Nenhuma sugestão nova. Para emuladores, escolha a pasta de saves à mão (ex.: RetroArch\\saves).";
            }
            catch (EmuSyncException) { TxtSugestoes.Text = "Não consegui ler as sugestões do EmuSync. Preencha à mão."; }
        }

        private void CmbSugestoes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSugestoes.SelectedItem is not EsSugestao s) return;
            EdNomeJogo.Text = s.Nome;
            if (_local != null && _camposPasta.TryGetValue(_local.Id, out var campo) && s.Pastas.Count > 0) campo.Text = s.Pastas[0];
            if (s.Pastas.Count > 1) TxtSugestoes.Text = "Esse jogo tem mais de uma pasta possível:\n" + string.Join("\n", s.Pastas.Take(4)) + "\nUsei a primeira; troque se precisar.";
        }

        private void EscolherPasta(TextBox campo)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta de saves", Multiselect = false };
            if (!string.IsNullOrWhiteSpace(campo.Text) && Directory.Exists(campo.Text)) dlg.InitialDirectory = campo.Text;
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) campo.Text = dlg.FolderName;
        }

        private async void BtnSalvarJogo_Click(object sender, RoutedEventArgs e) => await SalvarJogo(false);
        private async void BtnSalvarSincronizar_Click(object sender, RoutedEventArgs e) => await SalvarJogo(true);
        private async void BtnExcluirJogo_Click(object sender, RoutedEventArgs e) { if (_editando != null) await ExcluirJogo(_editando); }

        private async Task SalvarJogo(bool sincronizar)
        {
            var nome = (EdNomeJogo.Text ?? "").Trim();
            if (nome.Length == 0) { ToastWindow.Mostrar("Dê um nome ao jogo", "Ex.: RetroArch, PCSX2, Hollow Knight", "⚠"); EdNomeJogo.Focus(); return; }

            int? maxBackups = null;
            var mb = (EdMaxBackups.Text ?? "").Trim();
            if (mb.Length > 0)
            {
                if (!int.TryParse(mb, out var n) || n < 0) { ToastWindow.Mostrar("Backups inválido", "Use um número (0 ou mais), ou deixe vazio.", "⚠"); return; }
                maxBackups = n;
            }

            // mantém pastas de aparelhos que não aparecem na lista (ex.: removidos) para não apagar dados de outros
            var pastas = new Dictionary<string, string>(_editando?.Pastas ?? new Dictionary<string, string>());
            foreach (var kv in _camposPasta)
            {
                var v = (kv.Value.Text ?? "").Trim();
                if (v.Length == 0) pastas.Remove(kv.Key); else pastas[kv.Key] = v;
            }
            var minha = _local != null && pastas.TryGetValue(_local.Id, out var p) ? p : null;
            if (minha != null && !Directory.Exists(minha))
            {
                var r = MessageBox.Show(Window.GetWindow(this), $"A pasta deste PC não existe:\n{minha}\n\nSalvar mesmo assim? (o EmuSync cria a pasta ao baixar o save)", "EmuSync", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
            if (_editando == null && _jogos.Any(x => string.Equals(x.Nome, nome, StringComparison.CurrentCultureIgnoreCase)))
            {
                if (MessageBox.Show(Window.GetWindow(this), $"Já existe um jogo chamado “{nome}”. Criar outro com o mesmo nome?", "EmuSync", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }

            BtnSalvarJogo.IsEnabled = BtnSalvarSincronizar.IsEnabled = false;
            Trabalhando("Salvando no EmuSync…");
            try
            {
                string id;
                if (_editando == null) id = (await EmuSyncClient.CriarJogo(nome, EdAutoSync.IsChecked == true, pastas, maxBackups)).Id;
                else { id = _editando.Id; await EmuSyncClient.SalvarJogo(id, nome, EdAutoSync.IsChecked == true, pastas, maxBackups); }
                ToastWindow.Mostrar(_editando == null ? "Jogo adicionado" : "Jogo salvo", nome, "✔");

                if (sincronizar && minha != null)
                {
                    Trabalhando("Sincronizando " + nome + "…");
                    await EmuSyncClient.Sincronizar(id);
                    ToastWindow.Mostrar(nome, "Sincronizado", "✔");
                }
                MostrarPainel(_aba);
            }
            catch (EmuSyncException ex) { MostrarErro("Não consegui salvar", ex.Message); }
            finally
            {
                Trabalhando(null);
                BtnSalvarJogo.IsEnabled = BtnSalvarSincronizar.IsEnabled = true;
                await Carregar(false);
            }
        }

        // ================= BACKUPS =================
        private EsJogo _jogoBackups;

        private async Task AbrirBackups(EsJogo j)
        {
            _jogoBackups = j;
            TxtTituloBackups.Text = "Backups de “" + j.Nome + "”";
            ListaBackups.Children.Clear();
            ListaBackups.Children.Add(new TextBlock { Text = "Carregando…", Style = Estilo("Sub") });
            MostrarPainel("backups");
            await RenderizarBackups();
        }

        private async Task RenderizarBackups()
        {
            var j = _jogoBackups;
            if (j == null) return;
            List<EsBackup> lista;
            try { lista = (await EmuSyncClient.Backups(j.Id)).OrderByDescending(b => b.CriadoUtc).ToList(); }
            catch (EmuSyncException ex) { ListaBackups.Children.Clear(); ListaBackups.Children.Add(new TextBlock { Text = ex.Message, Style = Estilo("Sub"), TextWrapping = TextWrapping.Wrap }); return; }

            ListaBackups.Children.Clear();
            if (lista.Count == 0) { ListaBackups.Children.Add(CartaoVazio("Nenhum backup ainda", "O EmuSync cria um backup do save deste PC sempre que baixa um save da nuvem.")); return; }
            foreach (var b in lista)
            {
                var linha = new Border { Style = Estilo("Card"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 12, 16, 12) };
                var dock = new DockPanel();
                var botoes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(botoes, Dock.Right);
                var restaurar = new Button { Style = Estilo("BtnPrimario"), Content = "↩  Restaurar", Padding = new Thickness(12, 6, 12, 6) };
                var apagar = new Button { Style = Estilo("BtnPerigo"), Content = "Apagar", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
                var backup = b;
                restaurar.Click += async (s, e) =>
                {
                    var msg = $"Restaurar o backup de {backup.CriadoUtc.ToLocalTime():dd/MM/yyyy 'às' HH:mm}?\n\nO save atual de “{j.Nome}” neste PC é substituído por este backup, e ele é enviado para a nuvem (os outros aparelhos recebem no próximo sync).\nFeche o jogo antes.";
                    if (MessageBox.Show(Window.GetWindow(this), msg, "EmuSync", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    restaurar.IsEnabled = apagar.IsEnabled = false;
                    Trabalhando("Restaurando o backup…");
                    try { await EmuSyncClient.RestaurarBackup(j.Id, backup.Id); ToastWindow.Mostrar("Backup restaurado", j.Nome + " · enviado para a nuvem", "↩"); }
                    catch (EmuSyncException ex) { MostrarErro("Não consegui restaurar", ex.Message); }
                    finally { Trabalhando(null); await RenderizarBackups(); }
                };
                apagar.Click += async (s, e) =>
                {
                    if (MessageBox.Show(Window.GetWindow(this), "Apagar este backup? Não dá para desfazer.", "EmuSync", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    try { await EmuSyncClient.ExcluirBackup(j.Id, backup.Id); ToastWindow.Mostrar("Backup apagado", null, "🗑"); }
                    catch (EmuSyncException ex) { MostrarErro("Não consegui apagar", ex.Message); }
                    await RenderizarBackups();
                };
                botoes.Children.Add(restaurar); botoes.Children.Add(apagar);
                dock.Children.Add(botoes);
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(new TextBlock { Text = "🗂  " + b.CriadoUtc.ToLocalTime().ToString("dd/MM/yyyy 'às' HH:mm"), FontWeight = FontWeights.SemiBold });
                txt.Children.Add(new TextBlock { Text = Relativo(b.CriadoUtc) + " · " + b.Arquivo, Style = Estilo("Sub"), TextTrimming = TextTrimming.CharacterEllipsis });
                dock.Children.Add(txt);
                linha.Child = dock;
                ListaBackups.Children.Add(linha);
            }
        }

        // ================= APARELHOS =================
        private void RenderizarAparelhos()
        {
            ListaAparelhos.Children.Clear();
            if (_local == null) return;
            if (_aparelhos.Count == 0) { ListaAparelhos.Children.Add(CartaoVazio("Nenhum aparelho", "Conecte uma nuvem na aba Configurar.")); return; }
            foreach (var a in _aparelhos.OrderBy(x => x.Id != _local.Id).ThenBy(x => x.Nome, StringComparer.CurrentCultureIgnoreCase))
            {
                bool esteMesmo = a.Id == _local.Id;
                int jogos = _jogos.Count(j => !string.IsNullOrWhiteSpace(j.PastaDe(a.Id)));
                var linha = new Border { Style = Estilo("Card"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 12, 16, 12) };
                var dock = new DockPanel();
                if (!esteMesmo)
                {
                    var rem = new Button { Style = Estilo("BtnPerigo"), Content = "Remover", Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center,
                                           ToolTip = "Tira o aparelho da lista do EmuSync (use para aparelhos que você não tem mais)" };
                    var ap = a;
                    rem.Click += async (s, e) =>
                    {
                        var msg = $"Remover “{ap.Nome}” da lista do EmuSync?\n\nUse só para aparelhos que você não usa mais. Os saves dele não são apagados; se ele abrir o EmuSync de novo, volta para a lista.";
                        if (MessageBox.Show(Window.GetWindow(this), msg, "EmuSync", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                        try { await EmuSyncClient.ExcluirAparelho(ap.Id); ToastWindow.Mostrar("Aparelho removido", ap.Nome, "🗑"); }
                        catch (EmuSyncException ex) { MostrarErro("Não consegui remover", ex.Message); }
                        await Carregar(false);
                    };
                    DockPanel.SetDock(rem, Dock.Right);
                    dock.Children.Add(rem);
                }
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var titulo = new WrapPanel();
                titulo.Children.Add(new TextBlock { Text = a.Icone + "  " + a.Nome, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                if (esteMesmo) titulo.Children.Add(Chip("este PC", Recurso("AccentBrush")));
                txt.Children.Add(titulo);
                txt.Children.Add(new TextBlock { Text = $"{a.Plataforma} · {NomeProvedor(a.Provedor)} · {jogos} jogo(s) com pasta", Style = Estilo("Sub") });
                dock.Children.Add(txt);
                linha.Child = dock;
                ListaAparelhos.Children.Add(linha);
            }
        }

        // ================= HISTÓRICO =================
        private async Task RenderizarHistorico()
        {
            ListaHistorico.Children.Clear();
            ListaHistorico.Children.Add(new TextBlock { Text = "Carregando…", Style = Estilo("Sub") });
            List<EsLog> logs;
            try { logs = (await EmuSyncClient.Historico()).OrderByDescending(l => l.QuandoUtc).Take(150).ToList(); }
            catch (EmuSyncException ex) { ListaHistorico.Children.Clear(); ListaHistorico.Children.Add(new TextBlock { Text = ex.Message, Style = Estilo("Sub") }); return; }
            ListaHistorico.Children.Clear();
            if (logs.Count == 0) { ListaHistorico.Children.Add(new TextBlock { Text = "Nada sincronizado neste PC ainda.", Style = Estilo("Sub") }); return; }
            var nomes = _jogos.ToDictionary(j => j.Id, j => j.Nome);
            string diaAnterior = null;
            foreach (var l in logs)
            {
                var local = l.QuandoUtc.ToLocalTime();
                var dia = local.Date == DateTime.Today ? "Hoje" : local.Date == DateTime.Today.AddDays(-1) ? "Ontem" : local.ToString("dd/MM/yyyy");
                if (dia != diaAnterior)
                {
                    ListaHistorico.Children.Add(new TextBlock { Text = dia.ToUpperInvariant(), Style = Estilo("Rotulo"), Margin = new Thickness(0, diaAnterior == null ? 0 : 14, 0, 6) });
                    diaAnterior = dia;
                }
                var linha = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var hora = new TextBlock { Text = local.ToString("HH:mm"), Width = 54, Foreground = Recurso("MutedBrush") };
                DockPanel.SetDock(hora, Dock.Left);
                linha.Children.Add(hora);
                var extra = new TextBlock { Text = l.Automatico ? "automático" : "manual", Foreground = Recurso("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(extra, Dock.Right);
                linha.Children.Add(extra);
                var nome = nomes.TryGetValue(l.JogoId, out var n) ? n : "(jogo excluído)";
                linha.Children.Add(new TextBlock { Text = (l.Tipo == 1 ? "⬆  Enviou  " : "⬇  Baixou  ") + nome, TextTrimming = TextTrimming.CharacterEllipsis });
                ListaHistorico.Children.Add(linha);
            }
        }

        // ================= CONFIGURAR =================
        private void RenderizarConfigurar()
        {
            if (_local == null) return;
            bool conectado = _local.Provedor != EsProvedor.Nenhum;
            TxtProvedor.Text = conectado ? "☁  Conectado: " + NomeProvedor(_local.Provedor) : "☁  Nenhuma nuvem conectada neste PC";
            TxtProvedorDet.Text = conectado
                ? "Os saves ficam na pasta EmuSync_Data dessa nuvem. Todos os aparelhos precisam usar a mesma."
                : "Escolha onde guardar os saves. Ao clicar, o navegador abre para você entrar na conta; depois é só voltar aqui.";
            BotoesProvedor.Visibility = conectado ? Visibility.Collapsed : Visibility.Visible;
            BtnDesconectar.Visibility = conectado ? Visibility.Visible : Visibility.Collapsed;
            if (conectado) BlocoPastaRede.Visibility = Visibility.Collapsed;

            EdNomeAparelho.Text = _local.Nome;
            EdFrequencia.Text = _local.FrequenciaMin.ToString();
            EdBackupsPadrao.Text = _local.MaxBackups.ToString();

            var c = ConfigService.Atual;
            ChkCombo.IsChecked = c.EmuSyncComboControle;
            _ = AtualizarVarredura();
        }

        private async Task AtualizarVarredura()
        {
            TxtVarredura.Text = "…";
            try
            {
                var v = await EmuSyncClient.Varredura();
                TxtVarredura.Text = v.EmAndamento ? $"Procurando… {v.Percentual:0}%"
                    : $"{v.JogosEncontrados} jogo(s) de PC encontrados" + (v.UltimaHaSegundos < 0 ? " · última procura " + Duracao(-v.UltimaHaSegundos) + " atrás" : "");
            }
            catch (EmuSyncException) { TxtVarredura.Text = "Não consegui ler o estado da procura."; }
        }

        private async void BtnVarrer_Click(object sender, RoutedEventArgs e)
        {
            try { await EmuSyncClient.ForcarVarredura(); ToastWindow.Mostrar("Procurando jogos", "O EmuSync vai procurar em segundo plano. Pode levar alguns minutos.", "🔍"); }
            catch (EmuSyncException ex) { MostrarErro("Não consegui", ex.Message); }
            await Task.Delay(1500);
            await AtualizarVarredura();
        }

        private async void BtnSalvarAparelho_Click(object sender, RoutedEventArgs e)
        {
            var nome = (EdNomeAparelho.Text ?? "").Trim();
            if (nome.Length == 0) { ToastWindow.Mostrar("Dê um nome ao aparelho", null, "⚠"); return; }
            if (!int.TryParse(EdFrequencia.Text, out var freq) || freq < 1) { ToastWindow.Mostrar("Frequência inválida", "Use 1 minuto ou mais.", "⚠"); return; }
            if (!int.TryParse(EdBackupsPadrao.Text, out var bk) || bk < 0) { ToastWindow.Mostrar("Backups inválido", "Use 0 ou mais.", "⚠"); return; }
            try
            {
                await EmuSyncClient.SalvarLocal(nome, freq, bk);
                ToastWindow.Mostrar("Configurações do EmuSync salvas", nome, "✔");
            }
            catch (EmuSyncException ex) { MostrarErro("Não consegui salvar", ex.Message); }
            await Carregar(false);
        }

        private void ChkIntegracao_Click(object sender, RoutedEventArgs e)
        {
            var c = ConfigService.Atual;
            c.EmuSyncComboControle = ChkCombo.IsChecked == true;
            ConfigService.Salvar(true);
        }

        private async void BtnConectar_Click(object sender, RoutedEventArgs e)
        {
            var p = (EsProvedor)int.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture);
            if (p == EsProvedor.PastaCompartilhada) { BlocoPastaRede.Visibility = BlocoPastaRede.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; return; }
            try
            {
                var url = await EmuSyncClient.UrlLogin(p);
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new EmuSyncException(0, "O EmuSync não devolveu um endereço de login válido.");
                ProcessService.Abrir(url);
                Trabalhando($"Esperando você entrar no {NomeProvedor(p)} pelo navegador…");
                // o próprio EmuSync recebe a resposta do login (localhost:5353); aqui só esperamos ele ficar conectado
                var fim = DateTime.UtcNow.AddMinutes(5);
                while (DateTime.UtcNow < fim)
                {
                    await Task.Delay(2000);
                    try { if ((await EmuSyncClient.Local()).Provedor != EsProvedor.Nenhum) { ToastWindow.Mostrar("Nuvem conectada", NomeProvedor(p), "☁"); break; } }
                    catch (EmuSyncException) { }
                }
            }
            catch (EmuSyncException ex) { MostrarErro("Não consegui abrir o login", ex.Message); }
            finally { Trabalhando(null); await Carregar(false); }
        }

        private async void BtnConectarPastaRede_Click(object sender, RoutedEventArgs e)
        {
            var caminho = (EdPastaRede.Text ?? "").Trim();
            if (caminho.Length == 0) { ToastWindow.Mostrar("Escolha a pasta", "Ex.: \\\\NAS\\saves", "⚠"); return; }
            Trabalhando("Conectando à pasta…");
            try
            {
                await EmuSyncClient.ConectarPastaCompartilhada(caminho, EdUsuarioRede.Text, EdSenhaRede.Password);
                EdSenhaRede.Clear();
                ToastWindow.Mostrar("Pasta conectada", caminho, "☁");
            }
            catch (EmuSyncException ex) { MostrarErro("Não consegui conectar à pasta", ex.Message); }
            finally { Trabalhando(null); await Carregar(false); }
        }

        private async void BtnDesconectar_Click(object sender, RoutedEventArgs e)
        {
            var msg = $"Desconectar a nuvem ({NomeProvedor(_local?.Provedor ?? EsProvedor.Nenhum)}) deste PC?\n\nEste PC para de sincronizar. Nada é apagado da nuvem nem dos outros aparelhos; dá para conectar de novo depois.";
            if (MessageBox.Show(Window.GetWindow(this), msg, "EmuSync", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Trabalhando("Desconectando…");
            try { await EmuSyncClient.Desconectar(); ToastWindow.Mostrar("Nuvem desconectada", null, "☁"); }
            catch (EmuSyncException ex) { MostrarErro("Não consegui desconectar", ex.Message); }
            finally { Trabalhando(null); await Carregar(false); }
        }

        // ================= AJUDANTES =================
        private void Trabalhando(string texto)
        {
            BannerTrabalhando.Visibility = texto == null ? Visibility.Collapsed : Visibility.Visible;
            TxtTrabalhando.Text = texto ?? "";
        }

        private void MostrarErro(string titulo, string msg)
        {
            Log.Warn("EmuSync: " + titulo + " — " + msg);
            ToastWindow.Mostrar(titulo, msg, "⚠");
        }

        private Style Estilo(string chave) => (Style)FindResource(chave);
        private Brush Recurso(string chave) => (Brush)FindResource(chave);

        private Border CartaoVazio(string titulo, string sub)
        {
            var b = new Border { Style = Estilo("Card"), Padding = new Thickness(18, 16, 18, 16) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = titulo, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = sub, Style = Estilo("Sub"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            b.Child = sp;
            return b;
        }

        private Border Chip(string texto, Brush cor)
        {
            var c = cor is SolidColorBrush s ? s.Color : Colors.Gray;
            return new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)),
                Child = new TextBlock { Text = texto, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = cor }
            };
        }

        private (string, Brush) DescreverStatus(EsStatus s) => s switch
        {
            EsStatus.EmDia => ("✔ Em dia", Recurso("AccentBrush")),
            EsStatus.PrecisaEnviar => ("⬆ Enviar para a nuvem", new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xFF))),
            EsStatus.PrecisaBaixar => ("⬇ Baixar da nuvem", new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15))),
            EsStatus.SemPasta => ("Sem pasta neste PC", Recurso("MutedBrush")),
            _ => ("Verificando…", Recurso("MutedBrush"))
        };

        private string NomeAparelho(string id)
        {
            if (_local != null && id == _local.Id) return "este PC";
            return _aparelhos.FirstOrDefault(a => a.Id == id)?.Nome ?? "outro aparelho";
        }

        public static string NomeProvedor(EsProvedor p) => p switch
        {
            EsProvedor.GoogleDrive => "Google Drive",
            EsProvedor.Dropbox => "Dropbox",
            EsProvedor.OneDrive => "OneDrive",
            EsProvedor.PastaCompartilhada => "Pasta da rede",
            _ => "sem nuvem"
        };

        private static string Relativo(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalSeconds < 60) return "agora há pouco";
            if (d.TotalMinutes < 60) return $"há {(int)d.TotalMinutes} min";
            if (d.TotalHours < 24) return $"há {(int)d.TotalHours} h";
            if (d.TotalDays < 30) return $"há {(int)d.TotalDays} dia(s)";
            return "em " + utc.ToLocalTime().ToString("dd/MM/yyyy");
        }

        private static string Duracao(double segundos)
        {
            if (segundos < 60) return $"{(int)segundos} s";
            if (segundos < 3600) return $"{(int)(segundos / 60)} min";
            if (segundos < 86400) return $"{(int)(segundos / 3600)} h";
            return $"{(int)(segundos / 86400)} dia(s)";
        }

        private static string Tamanho(long b) =>
            b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" : b >= 1024 ? $"{b / 1024.0:0} KB" : $"{b} B";
    }
}
