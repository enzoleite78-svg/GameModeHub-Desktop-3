# GameModeHub 4.1 · Remaster — o que mudou

Tudo o que existia na 4.0 continua funcionando, e o `config.json` antigo (3.x e 4.0) é lido normalmente.
No código, cada correção está marcada com `CORREÇÃO 4.1` e cada novidade com `NOVO 4.1` / `MELHORIA 4.1`.

## 🐞 Bugs corrigidos

| # | Onde | Problema | Como ficou |
|---|---|---|---|
| 1 | csproj | `InvariantGlobalization=true` gerava erro no WPF ("Cannot find non-neutral culture…") | Desligado |
| 2 | Celular | O QR na tela não mudava quando o código vencia (10 min) ou depois de um pareamento, e o celular recebia "código incorreto" | QR se atualiza sozinho + contagem de validade |
| 3 | Servidor | O app antigo do celular usa `nivel`, `nome`, `dy`, `delta`, `cmd`, mas o servidor só aceitava `v`, `exe`, `d`, `tecla`. Por isso volume, foco de janela, rolagem, zoom e Backspace não funcionavam | Aceita os dois jeitos |
| 4 | Servidor | Rolagem/zoom pelo celular quase não andavam (o Windows espera passos de 120) | Passos pequenos são convertidos |
| 5 | Servidor | `/kill` sem `exe` (app antigo) não fazia nada | Fecha o jogo em foco, como na v3 |
| 6 | Controle | A opção "abrir Modo TV ao segurar o Guia" era ignorada | Respeitada (e tem interruptor na tela) |
| 7 | Controle | Se o controle já estava conectado antes da janela abrir, ela mostrava "Nenhum controle" | Mostra o estado real |
| 8 | Restaurar | Sempre forçava o plano "Equilibrado", mesmo se você usava outro | Volta para o plano que estava antes |
| 9 | Restaurar | Desligava o HDR de todas as telas, até de quem usa HDR no dia a dia | Só desliga se foi o modo que ligou |
| 10 | Restaurar | Usava um layout de telas "pc" antigo mesmo depois de você mudar ou excluir aquele modo | Só usa se o modo ainda usa; e agora dá para escolher |
| 11 | Restaurar | Reabria programas duplicados se você já tinha aberto de novo | Pula os que já estão abertos |
| 12 | Modos | Ao reabrir o app no meio de um modo, ele dizia "Desktop normal" e o Restaurar perdia o que tinha que desfazer | O estado fica salvo em disco |
| 13 | Energia | Em muitos notebooks/Windows 11 o "Alto desempenho" vem escondido e a troca falhava calada (e ainda aparecia "Energia: alto") | Tenta "Desempenho máximo", recria o plano se precisar e avisa quando não existe |
| 14 | Firewall | A regra só valia em rede "Particular". Depois de formatar, a rede vem como "Pública" e o celular não conectava (a limitação descrita no documento) | A regra vale em qualquer rede, mas só para aparelhos da mesma rede local. Também remove bloqueios criados quando alguém clica em "Cancelar" no aviso do Windows |
| 15 | Firewall | Trocando a porta, o app continuava dizendo "liberado" (a regra era da porta antiga) | A regra leva a porta no nome |
| 16 | Admin | "Reabrir como administrador" não funcionava: a cópia nova se fechava achando que era duplicada | Espera a cópia antiga fechar |
| 17 | Avisos (toast) | O temporizador fechava de novo uma janela já fechada, gerando erro no log a cada aviso | Corrigido, e o aviso agora aparece e some com animação |
| 18 | Papel de parede | Trocar por outra imagem do mesmo tipo continuava mostrando a antiga (cache do WPF) | Sempre carrega a nova; imagens velhas são apagadas |
| 19 | Papel de parede | A camada "escurecer" era aplicada mesmo sem imagem (fundo acinzentado) | Só com imagem; no tema claro ela clareia |
| 20 | Telas | "Atualizar lista" cortava o nome da tela na primeira palavra ("LG ULTRAGEAR" virava "LG") | Nome completo |
| 21 | Telas | Conferia o resultado uma vez só, após 600 ms. TVs lentas para ligar faziam o app desfazer uma troca que tinha dado certo | Confere até 4 vezes (~2,6 s) |
| 22 | Fechar jogo travado | Falhava com jogos que têm mais de uma janela e fechava todos os processos com o mesmo nome | Fecha o processo dono da janela em foco e solta o mouse |
| 23 | Programas | A lista de programas abertos travava a janela por 1–2 s | Carrega em segundo plano (e lê o caminho do .exe de forma muito mais rápida) |
| 24 | Programas | "Discord" e "discord" contavam como dois programas diferentes | Sem diferença de maiúsculas |
| 25 | Programas | Fechar "educadamente" esperava 4 s à toa em apps que só ficam na bandeja | Só espera se o app tinha janela |
| 26 | Rede | O QR às vezes saía com o IP da VPN, do Hyper-V ou um 169.254 (sem rede) | Escolhe a placa de rede real |
| 27 | Config | Arquivo corrompido voltava tudo ao padrão | Tenta o backup `.bak` primeiro; valores nulos/inválidos são corrigidos |
| 28 | Config | Salvar falhava em alguns pendrives/pastas sincronizadas | Tem um plano B de gravação |
| 29 | Portátil | "Iniciar com o Windows" apontava para o lugar antigo se você movesse a pasta | Caminho atualizado a cada abertura |
| 30 | Editor | "Outro…" sem programa escolhido salvava e mostrava "Abrindo Outro…" | Pede para escolher o programa |
| 31 | Atalhos | Atalho repetido ou já usado por outro programa falhava sem aviso | Avisa na hora (no editor, ao salvar e ao abrir o app) |
| 32 | CPU | Janela e celular liam a CPU ao mesmo tempo e o valor pulava para 0 ou 100 | Leitura compartilhada |
| 33 | Controle | Apertar A num botão de opção (ex.: áudio A/B) marcava mas não aplicava | Aplica |
| 34 | Escurecer | O slider não fazia nada se o escurecimento estivesse desligado no config | Funciona sempre |

## ✨ Funções novas

- **Perfis de verdade**: seletor na barra lateral e na bandeja; criar (cópia), renomear e excluir em Geral. A v3 tinha perfis e o remake só guardava no config.
- **Combos do controle de volta** (eram da v3 e sumiram no remake): LB+RB+Start (modo padrão), +Back (restaurar), +◀ (fechar jogo travado), +▶ (trocar áudio), +▲/▼ (volume). Tem interruptor para ligar/desligar e vibração de confirmação.
- **Vibração do controle** ao trocar de modo e nos combos (era da v3).
- **Temas**: Escuro, OLED, Azul-noite e Claro, mais **cor de destaque personalizada** (paleta completa). A barra de título do Windows acompanha o tema.
- **Gravar atalho apertando a tecla**: clique na caixa e aperte a combinação, sem precisar digitar "NumPad1".
- **Atalhos globais novos**: "Fechar jogo travado" e "Abrir Modo TV".
- **Atalho na área de trabalho** que ativa um modo com dois cliques (`--modo`, `--restaurar`, `--tv`).
- **Menu da bandeja completo**: ativar qualquer modo, trocar perfil, restaurar, trocar áudio, Modo TV, acender telas. Mostra o modo atual, e um clique no ícone já abre a janela.
- **Primeiros passos**: cartão na tela Início que guia a configuração e some quando termina.
- **Indicador "Ativando…"** na janela e no Modo TV, com os botões travados enquanto troca (evita clique duplo).
- **Áudio**: botão de **mudo**, **▶ Testar** cada saída (toca um som só naquele aparelho) e volume que acompanha mudanças feitas fora do app.
- **Por modo**: *Liberar memória RAM* e *Tirar o cursor do meio da tela* (eram da v3, agora opcionais), além de **Duplicar** modo e escolher a cor que quiser.
- **Ao restaurar**: escolher quais telas (automático, todas, não mexer, telas de um modo) e qual áudio.
- **Silenciar notificações do Windows** durante os modos (beta, volta como estava ao restaurar). É o "Modo Foco" da v3.
- **Fecha o Big Picture da Steam** ao restaurar, se foi o modo que abriu (comportamento da v3).
- **Backup**: exportar/importar tudo num arquivo. Os pareamentos do PC são mantidos, por segurança.
- **Verificar atualizações** no GitHub, uma vez por dia ou manual. Só avisa e abre a página de download, não troca nada sozinho.
- **Papel de parede separado para o Modo TV**, com miniaturas.
- **Launcher "Steam" (janela normal)** além do Big Picture.
- **Modo TV**: mostra relógio, volume, bateria do controle e perfil, e já foca o modo ativo.
- **Servidor**: rotas `/audio/mudo`, `/audio/saida`, `/tv`, `/perfis`, `/perfil`; `/status` traz perfil, saídas, atalhos e se está ocupado; `/janela/focar` abre o programa se ele estiver fechado; clique do meio e clique duplo; mais teclas (F1–F24, Alt+F4, Win+D…).
- **Aviso ao fechar no X**: na primeira vez, avisa que o app continua na bandeja.
- **Log mais útil**: os erros mostram onde aconteceram.

## 🎨 Interface

- Nova página **Controle e atalhos**, que reúne tudo de controle e teclado, com a lista de todos os atalhos ativos e qual está com problema.
- Cartões de modo maiores: ícone colorido, etiquetas (ATIVO, atalho, combo do controle), borda na cor do modo ativo, menu no botão direito e um cartão "＋ Novo modo".
- Saudação, CPU/RAM no topo e ponto colorido do modo atual.
- Barras de rolagem finas, dicas (tooltips) escuras, menus arredondados, botões com efeito ao clicar, campos com texto de exemplo e barrinha de destaque no menu lateral.
- Confirmação antes de excluir modo, perfil ou celular pareado.
- Esc volta do editor, Ctrl+S salva e F5 atualiza a página.

## ➖ Removido

- Nada que funcionava foi removido.
- O código interno `XamlReaderHelper` (que montava as bolinhas de cor lendo XAML em texto toda vez) foi trocado por um estilo pronto no tema.
- A opção de config `ModoFocoAtivo` (da v3) continua no arquivo só por compatibilidade. Quem manda agora é "Silenciar notificações", que vem desligada para ninguém ser pego de surpresa.
