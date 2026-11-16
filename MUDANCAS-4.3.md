# GameModeHub Desktop 4.3 · Addons

## Novo: addons, começando pelo EmuSync

**Geral → Addons → EmuSync** liga o GameModeHub ao [EmuSync](https://github.com/emu-sync/EmuSync), que sincroniza os saves de emuladores e jogos entre PC, Steam Deck e celular pela nuvem (Google Drive, Dropbox, OneDrive ou uma pasta da rede).

Os dois continuam sendo **programas separados**. O EmuSync oficial faz a sincronização (pelo serviço dele, o `EmuSyncAgent`) e o GameModeHub só mostra e comanda, conversando com esse serviço pela API local (`localhost:5353`). As atualizações do EmuSync continuam vindo do autor.

- **Baixar e instalar:** baixa o instalador oficial do GitHub do EmuSync, abre o instalador e detecta sozinho quando ele termina.
- **Já tenho instalado:** procura o EmuSync no PC. Se o serviço estiver parado, oferece ligar.
- Quando o addon é ativado, aparece a aba **🔄 EmuSync** na barra lateral. "Tirar da barra lateral" só esconde a aba; o EmuSync continua funcionando.

### Aba EmuSync
- **Jogos:** estado de cada save (em dia, enviar, baixar, sem pasta), busca, filtros, sync com a porcentagem no botão e sync automático por jogo.
  - **⋯ de cada jogo:** editar, backups, abrir a pasta, forçar envio, forçar download e excluir.
- **Adicionar e editar jogo:** nome, pasta de saves de cada aparelho (com "Procurar…" neste PC), sugestões de jogos de PC encontrados pelo EmuSync e quantidade de backups.
- **Backups:** restaurar (volta o save deste PC e envia para a nuvem) e apagar.
- **Sincronizar tudo:** envia ou baixa tudo o que está diferente da nuvem. O save mais novo vence, a mesma regra do EmuSync.
- **Aparelhos:** todos os aparelhos ligados à nuvem, com o número de jogos de cada um. Dá para remover aparelhos antigos.
- **Histórico:** o que este PC enviou e baixou, separado por dia.
- **Configurar:**
  - conectar ou desconectar a nuvem;
  - nome do PC, intervalo do sync automático e backups padrão;
  - procura de jogos (Ludusavi).

### Junto com o resto do GameModeHub
- **Controle:** segurar **LB + RB + Y** por 1,5 s sincroniza tudo.
- **Bandeja:** item "Sincronizar saves (EmuSync)".
- **Celular**, rotas novas do servidor:
  - `/emusync`: estado e lista de jogos;
  - `/emusync/sync[?id=]`: sincroniza um jogo ou todos;
  - `/emusync/enviar?id=` e `/emusync/baixar?id=`: forçar envio ou download. Só aceitam POST.
