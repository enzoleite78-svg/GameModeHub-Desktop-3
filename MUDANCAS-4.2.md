# GameModeHub 4.2 — o que mudou (para o app novo do celular)

Use junto com o **GameModeHub Mobile 5.0**. O app antigo do celular não funciona mais: ele não tem pareamento.

## Novo no PC

- **Conexão em tempo real** (`/ws`, WebSocket) para mouse e teclado do celular, sem atraso.
- **Biblioteca de jogos**: lê os jogos instalados da Steam, Epic e GOG (`/jogos`, `/jogo/abrir`).
- **"Tocando agora"** pelo controle de mídia do Windows: título, artista, app, capa e tocar/pausar/próxima/anterior (`/midia…`).
- **Mixer por programa** e **escolher qualquer saída de áudio** (`/audio/apps`, `/audio/app`, `/audio/saidas`, `/audio/definir`).
- **Prévia da tela** para o celular, com clique na posição tocada (`/tela.jpg`, `/mouse/abs`).
- **Desempenho**: CPU, RAM e **GPU** (`/desempenho`).
- **Área de transferência** celular ⇄ PC e **abrir link no PC** (`/clipboard`).
- **Receber arquivos/fotos do celular**: salvos em `Downloads\GameModeHub` (`/arquivo`, até 300 MB).
- **Timer** de desligar/suspender/reiniciar com aviso 1 min antes (`/timer`).
- Teclado: **teclas com modificadores** (Ctrl/Alt/Shift/Win), letras, F1–F24, mais atalhos prontos; mouse: **segurar/soltar** (arrastar) e **rolagem horizontal**.
- `/status` agora informa também o **programa em foco** e o **timer**.
- **Avisos para o celular** (troca de modo, timer) pela conexão em tempo real.
- Tela **Celular**: opção **"Permitir controlar de fora de casa pela VPN"** (Tailscale/ZeroTier, faixa 100.64.0.0/10). Vem desligada.

## Tela Programas

- **Duas listas claras**: "Fecham ao ativar um modo" e "Ficam abertos (nunca fecham)", com um botão para passar de uma para a outra. Antes era um interruptor, e "desligado = não fecha" não ficava claro.
- **🔍 Procurar programas abertos**: acha o que está aberto agora, com janela ou só na bandeja (ex.: Discord minimizado), mostra o nome amigável e deixa decidir um por um: **Fechar no modo**, **Manter aberto** ou **Ignorar** (lembrado nas próximas varreduras).
- Escolher um .exe também para "manter aberto".
- Programas do Windows que nunca podem fechar (ex.: ApplicationFrameHost, que hospeda os apps da Loja) ficam protegidos.

## Ligar o PC pelo celular (Wake-on-LAN)

- Tela **Celular** → novo cartão que mostra se a placa de rede está pronta e o botão **Preparar a placa de rede** (pede permissão uma vez): liga o "magic packet" e o "acordar com o PC desligado" e desliga as economias de energia (Energia Eficiente Ethernet, Green Ethernet, Power Saving) que fazem a placa dormir demais.
- Explica o ajuste da BIOS (Wake on LAN / Power On By PCI-E ligado, ErP desligado).

## Segurança

- Aceita o token também em `Authorization: Bearer`.
- **Desligar, reiniciar, suspender, fechar programas, timer e envio de arquivos só por POST**: um link aberto sem querer não faz nada.
- Arquivo enviado só é gravado depois que o token é conferido.

## Correção

- O token mestre gerado no primeiro uso só era salvo quando outra configuração mudasse. Por isso o atalho da área de trabalho (`--modo`) podia não funcionar logo depois de instalar.

## Técnico

- Alvo `net10.0-windows10.0.19041.0` (necessário para ler a mídia do Windows). Continua rodando no Windows 10 1809 ou mais novo e no 11.
