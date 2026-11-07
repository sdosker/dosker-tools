# Compressor Exclusions

Escolha quais parâmetros o compressor do VRCFury pode comprimir, sem editar o pacote do VRCFury.

## O problema

Quando um avatar passa de 256 bits sincronizados, o VRCFury comprime parâmetros por conta própria, e não há opção para escolher quais. Parâmetros comprimidos sincronizam em lotes, então quem vê o avatar recebe a mudança com atraso.

Com esta ferramenta, os parâmetros que você protege continuam sincronizando direto. O VRCFury segue resolvendo o resto.

## Requisitos

- Unity 2022.3 com VRChat Avatars SDK
- VRCFury (testado na 1.1442.0), instalado pelo [listing oficial](https://vcc.vrcfury.com)

## Instalar

Adicione o listing do [Dosker Tools](https://sdosker.github.io/dosker-tools/) ao VRChat Creator Companion e instale este pacote no projeto. Para atualizar, use o próprio VCC: suas regras são mantidas, porque a configuração fica fora do pacote. Em um projeto novo, ela é criada automaticamente em `Assets/CompressorExclusions`, protegendo todos os Bools.

## Como usar

1. Abra **Tools > Dosker Tools > Compressor Exclusions**.
2. Faça um **Build & Test** do avatar. Antes dele, a janela mostra o avatar da cena com a marca **(cena)**. Os parâmetros de Toggles do VRCFury e de prefabs só existem durante o build, então a lista completa aparece depois dele.
3. Ajuste as regras e acompanhe o resumo e a **Previsão do build**, que mudam a cada ajuste.
4. Faça um novo build e compare as colunas **Previsto** e **Real**.
5. Envie **primeiro a versão de PC, depois a de Quest**. Sempre que a configuração mudar, envie as duas, nessa ordem, senão elas não sincronizam entre si.

## O que dá para configurar

A configuração fica em `CompressorExclusions.asset` e vale para o projeto inteiro. A regra inicial protege todos os Bools.

| Opção | Efeito |
|---|---|
| **Ativo** | Desligado, o VRCFury age como se a ferramenta não existisse |
| **Proteger todos os** | Protege por tipo de valor: Bool, Int ou Float |
| **Proteger controles** | Protege por controle do menu: Toggle, Radial, puppets |
| **Padrões de nome** | Protege por nome, com `*` e `?` como curingas. Ex.: `Fur*Off`, `Roupas/*` |
| **`menu:`** | Usa o caminho no menu em vez do nome. Ex.: `menu:Roupas/*` protege o que está no submenu Roupas |
| **`!`** | Libera de todas as regras gerais. Ex.: com a regra Bool ligada, `!Fur*` deixa esses Bools comprimirem |
| **Escolha individual** | Vale para um parâmetro e tem prioridade sobre as regras |

O prefixo `VF<número>_` que o VRCFury adiciona aos nomes é ignorado, porque o número muda entre builds. Por isso, dois parâmetros que só diferem nesse prefixo dividem a mesma escolha, e a janela avisa quando isso acontece.

Um parâmetro usado em mais de um controle conta como um só, seguindo o VRCFury. Usado em Radial e Toggle, ele conta como Toggle, então a regra **Radial** não o alcança.

### Escolha individual

| Estado | Significado |
|---|---|
| **Auto** | Segue as regras gerais |
| **Proteger** | Nunca comprimir |
| **Comprimir** | Pode comprimir, mesmo que uma regra o proteja |

Para liberar só um Bool com a regra Bool ativa, marque-o como **Comprimir**.

## Editando direto na lista de parâmetros

O inspector de qualquer asset **VRC Expression Parameters** ganha a coluna **Compressão**. Clique no botão da linha para alternar entre Auto, Proteger e Comprimir. A mudança aparece na hora na janela, e o contrário também. Ctrl+Z funciona nos dois.

A faixa colorida do botão mostra o efeito atual: verde para protegido e laranja para liberado. Passe o mouse para ver as regras que valem, o custo e o que aconteceu no último build. O botão **Abrir configuração**, na barra do inspector, abre a janela.

## Lendo a janela

- **Barra de bits:** custo estimado com a escolha atual. Fica vermelha se não couber.
- **Atraso estimado:** o tempo até quem vê o avatar receber uma mudança comprimida. O atraso não afeta a sua própria visão.
- **Último build:** o resultado real do VRCFury. Se divergir da estimativa, vale o real.
- **Sem compressão:** parâmetros que o VRCFury nunca comprime, como face tracking fora do menu, parâmetros com largura de bits ou codificação especial, ou que não precisaram ser comprimidos.
- **Coluna Situação:** mostra em qual lote cada parâmetro comprimido cai. Passe o mouse para ver todas as regras que valem para ele.
- **Fazer caber:** quando o avatar não cabe, sugere o menor conjunto de parâmetros protegidos a liberar. Nada muda antes de você confirmar, e Ctrl+Z desfaz.
- **Histórico de builds:** os últimos builds de cada avatar e plataforma, com quantos parâmetros entraram e saíram da compressão.

## Previsão do build

O cartão **Previsão do build** tem três colunas:

| Coluna | O que mostra |
|---|---|
| **Próximo build** | O que a configuração atual deve produzir |
| **Previsto** | O que se esperava do último build, salvo antes de ele rodar |
| **Real** | O que o VRCFury fez no último build |

Abaixo das colunas, a janela diz se a previsão acertou e lista os parâmetros que divergiram:

- **A previsão acertou:** tudo bateu.
- **Os parâmetros mudaram:** o avatar ganhou ou perdeu parâmetros desde o build anterior. Refeita com a lista nova, a previsão acertou, então o solver segue igual.
- **O resultado difere:** mesmo com a lista do próprio build, a previsão errou. Vale o resultado real: o VRCFury pode ter mudado o solver.

Antes do primeiro build, a previsão usa só os parâmetros que já estão no avatar selecionado ou no primeiro da cena e aparece como **parcial**, porque os de Toggles do VRCFury e de prefabs só existem durante o build.

## PC e Quest

Em Quest, o VRCFury reaproveita a decisão do último build de PC e normalmente nem chama o solver, então a janela costuma ter só o resultado de PC. Por isso, envie sempre PC primeiro: é ele que decide o que comprime.

Quando o solver roda em Quest, por não haver decisão de PC, o resultado fica separado no seletor de avatar. Nesse caso, a janela e o Console avisam se PC e Quest usaram configurações diferentes ou comprimiram parâmetros diferentes.

## Limites

Proteger não cria bits. Tudo o que você protege passa a ocupar seu custo inteiro, e 256 continua sendo o limite. Se não couber, a janela avisa antes e o VRCFury recusa o upload. Liberar números costuma resolver mais rápido, pois cada um economiza 8 bits, contra 1 de um Bool.

Proteger o controle **Toggle** tira também os números usados por toggles, e isso pode impedir o avatar de caber. Prefira proteger o tipo **Bool**.

## Quando atualizar o VRCFury

A ferramenta não altera o pacote, então a atualização não apaga nada. Ao carregar, ela confere se o VRCFury ainda tem o que precisa e avisa quando a versão é mais nova que a testada. Se a atualização quebrar o patch, a janela mostra **Patch com erro** e o build pergunta antes de continuar: o padrão é cancelar, para que o avatar não seja comprimido sem as proteções sem você saber. Em batch mode ninguém pode responder, então o build falha.

## Relatório

Para scripts ou relatos de bug, `-executeMethod CompressorExclusions.CompressorReport.Export` grava o estado e os últimos builds em `Logs/CompressorExclusionsReport.json`.

## Desinstalar

Remova o pacote pelo VCC. Nada fica no avatar. A configuração fica em `Assets/CompressorExclusions` e os retratos de build em `Library/CompressorExclusions`, e ambos podem ser apagados.

## Licença

Uso pessoal e não comercial, incluindo a proibição de vendas e doações, como explica o [LICENSE.md](LICENSE.md).
