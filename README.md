# GameModeHub Desktop 4.2 · Remaster

Controle de modos de jogo para o PC (telas, áudio, programas, energia, launcher) com o celular e o controle do Xbox como controle remoto.

> O que mudou: **[MUDANCAS-4.2.md](MUDANCAS-4.2.md)** (app novo do celular) e **[MUDANCAS-4.1.md](MUDANCAS-4.1.md)** (remaster).
> App do celular: use o **GameModeHub Mobile 5.0** (o app antigo não tem pareamento e não conecta).

## Como gerar o .exe (sem instalar nada no seu PC)

1. Crie um repositório no GitHub e envie esta pasta inteira.
2. Abra a aba **Actions** → **Build GameModeHub Desktop** → **Run workflow** (ou só faça um push).
3. Quando terminar, baixe o arquivo **GameModeHub-Desktop-portable** em *Artifacts*.
4. Extraia o ZIP onde quiser (pendrive, outro PC). Abra `GameModeHub.exe`. Não precisa instalar o .NET.

Se o build der erro, copie a mensagem vermelha do log e envie para ajustar.

Para compilar localmente: instale o SDK .NET 10 e rode `dotnet publish -c Release -o publish`.

Para publicar uma versão (e o botão **Verificar atualizações** do app avisar todo mundo): crie uma tag `v4.1.0` no GitHub.

## Portátil

- Se existir o arquivo `portable.flag` ao lado do `.exe` (o ZIP já vem com ele), tudo fica na pasta `data` ao lado do programa: config, layouts, log e papéis de parede.
- Sem o `portable.flag`, usa `%APPDATA%\GameModeHub` (o `config.json` das versões 3 e 4.0 é lido normalmente).

## Primeiro uso (2 minutos)

O cartão **✨ Primeiros passos** na tela Início guia você por estes itens e some quando tudo estiver pronto:

1. **Geral → Saídas de áudio**: escolha a saída A (fones) e B (TV/caixas). Use **▶ Testar** para saber qual aparelho é qual.
2. **Início**: clique no ✎ do modo **Jogar na TV** → *Só as que eu marcar* → marque **só a TV** → Salvar.
3. Faça o mesmo no modo **Jogar no Monitor**, marcando **os 2 monitores** e deixando a TV desmarcada.
4. **Celular**: clique em *Liberar no firewall* (uma vez) e leia o QR no app.
5. **Programas**: adicione o que deve fechar ao entrar num modo (navegador, Discord…).

## Jeitos de ativar um modo

| Onde | Como |
|---|---|
| Janela | Clique no cartão do modo (botão direito: mais opções) |
| Bandeja (perto do relógio) | Botão direito → **Ativar modo** |
| Teclado (até dentro do jogo) | Atalho do modo (ex.: `NumPad1`) — grave no ✎ do modo |
| Controle Xbox | Segure **LB + RB + Start** (2 s) · ou abra o **Modo TV** segurando o botão Xbox (3,5 s) |
| Área de trabalho | ✎ do modo → **Atalho na área de trabalho** |
| Celular | App GameModeHub |

### Combos do controle (segurando LB + RB)

| Combo | Ação |
|---|---|
| Start (2 s) | Ativa o modo escolhido em *Controle e atalhos* |
| Back (1,5 s) | Restaura o desktop |
| ◀ (1,5 s) | Fecha o jogo travado em foco e solta o mouse |
| ▶ | Troca a saída de áudio A ⇄ B |
| ▲ / ▼ | Volume +5% / −5% |

## Rotas do servidor (para o app do celular)

Todas exigem o cabeçalho `X-GMH-Token` (ou `?t=`), exceto `/ping` e `/pair`. Requisições com cabeçalho `Origin` (sites) são bloqueadas; só aceita a rede local.

`/ping` · `/pair?code=123456&nome=Celular` · `/status` · `/apps` · `/modo/{id}` · `/restaurar` · `/audio/toggle` · `/audio/saida?slot=A|B` · `/audio/volume?v=0-100` (ou `?d=±5`) · `/audio/mudo` (ou `?v=0|1`) · `/kill?exe=nome` (sem `exe` = jogo em foco) · `/kill/ativo` · `/alttab` · `/janela/focar?exe=nome` (abre se estiver fechado) · `/mouse/move?dx=&dy=` · `/mouse/click?btn=left|right|middle&duplo=1` · `/mouse/scroll?d=` · `/mouse/zoom?d=` · `/teclado?texto=` ou `?tecla=enter|backspace|tab|esc|f1…f24|altf4|desktop|...` · `/tv` · `/perfis` · `/perfil?id=` · `/suspender` · `/desligar` · `/reiniciar`

**Compatível com o app antigo do celular:** também aceita `nivel`, `nome`, `dy`, `delta` e `cmd` (nomes de parâmetro da v3).

O QR contém `{ip, porta, mac, pc, code}`; o celular troca o `code` por um token próprio em `/pair`, que pode ser revogado em **Celular → Aparelhos pareados**. O código vale 10 minutos ou um pareamento.

## Linha de comando

`GameModeHub.exe --modo <id>` · `--restaurar` · `--tv` · `--minimizado`. Se o app já estiver aberto, o comando é repassado para ele.

## Observações

- HDR, troca de telas e áudio dependem do driver/hardware; teste cada modo uma vez (**Salvar e testar agora**). Se algo ficar errado: **Acender todas as telas** (Início ou bandeja) ou o atalho de restaurar.
- "Silenciar notificações do Windows" está marcado como *beta*: depende da versão do Windows.
- Log em `data\gamemodehub.log` (**Geral → Ver log**).
