# Compressor Exclusions

Escolha quais parâmetros o compressor do VRCFury pode comprimir, sem editar o pacote do VRCFury.

## O problema

Quando um avatar passa de 256 bits sincronizados, o VRCFury comprime parâmetros por conta própria, e não há opção para escolher quais. Parâmetros comprimidos sincronizam em lotes, então quem vê o avatar recebe a mudança com atraso.

Com esta ferramenta, os parâmetros que você protege continuam sincronizando direto. O VRCFury segue resolvendo o resto.

## Requisitos

- Unity 2022.3 com VRChat Avatars SDK
- VRCFury (testado na 1.1435.0), instalado pelo [listing oficial](https://vcc.vrcfury.com)

## Instalar

Adicione o listing do [Dosker Tools](https://sdosker.github.io/dosker-tools/) ao VRChat Creator Companion e instale este pacote no projeto. Para atualizar, use o próprio VCC: suas regras são mantidas, porque a configuração fica fora do pacote. Em um projeto novo, ela é criada automaticamente em `Assets/CompressorExclusions`, protegendo todos os Bools.

## Como usar

1. Abra **Tools > Dosker Tools > Compressor Exclusions**.
2. Faça um **Build & Test** do avatar. Os parâmetros de Toggles do VRCFury e de prefabs só existem durante o build, então a tabela é preenchida depois dele.
3. Ajuste as regras e acompanhe o resumo, que mostra os bits e o atraso estimado a cada mudança.
4. Faça um novo build. O card **Último build** mostra o resultado real.
5. Envie **primeiro a versão de PC, depois a de Quest**. Sempre que a configuração mudar, envie as duas, nessa ordem, senão elas não sincronizam entre si.

## O que dá para configurar

A configuração fica em `CompressorExclusions.asset` e vale para o projeto inteiro. A regra inicial protege todos os Bools.

| Opção | Efeito |
|---|---|
| **Ativo** | Desligado, o VRCFury age como se a ferramenta não existisse |
| **Proteger todos os** | Protege por tipo de valor: Bool, Int ou Float |
| **Proteger controles** | Protege por controle do menu: Toggle, Radial, puppets |
| **Padrões de nome** | Protege por nome, com `*` como curinga. Ex.: `Fur*Off`, `Roupas/*` |
| **Escolha individual** | Vale para um parâmetro e tem prioridade sobre as regras |

O prefixo `VF<número>_` que o VRCFury adiciona aos nomes é ignorado, porque o número muda entre builds.

### Escolha individual

| Estado | Significado |
|---|---|
| **Auto** | Segue as regras gerais |
| **Proteger** | Nunca comprimir |
| **Comprimir** | Pode comprimir, mesmo que uma regra o proteja |

Para liberar só um Bool com a regra Bool ativa, marque-o como **Comprimir**.

## Editando direto na lista de parâmetros

O inspector de qualquer asset **VRC Expression Parameters** ganha a coluna **Compressão**. Clique no botão da linha para alternar entre Auto, Proteger e Comprimir. A mudança aparece na hora na janela, e o contrário também. Ctrl+Z funciona nos dois.

A faixa colorida do botão mostra o efeito atual: verde para protegido e laranja para liberado. O botão **Abrir configuração**, na barra do inspector, abre a janela.

## Lendo a janela

- **Barra de bits:** custo estimado com a escolha atual. Fica vermelha se não couber.
- **Atraso estimado:** o tempo até quem vê o avatar receber uma mudança comprimida. O atraso não afeta a sua própria visão.
- **Último build:** o resultado real do VRCFury. Se divergir da estimativa, vale o real.
- **Sem compressão:** parâmetros que o VRCFury nunca comprime, como face tracking fora do menu, ou que não precisaram ser comprimidos.

## Limites

Proteger não cria bits. Tudo o que você protege passa a ocupar seu custo inteiro, e 256 continua sendo o limite. Se não couber, a janela avisa antes e o VRCFury recusa o upload. Liberar números costuma resolver mais rápido, pois cada um economiza 8 bits, contra 1 de um Bool.

Proteger o controle **Toggle** tira também os números usados por toggles, e isso pode impedir o avatar de caber. Prefira proteger o tipo **Bool**.

## Quando atualizar o VRCFury

A ferramenta não altera o pacote, então a atualização não apaga nada. Se a atualização quebrar o patch, a janela mostra **Patch com erro** e o build pergunta antes de continuar: o padrão é cancelar, para que o avatar não seja comprimido sem as proteções sem você saber.

## Desinstalar

Remova o pacote pelo VCC. Nada fica no avatar. A configuração fica em `Assets/CompressorExclusions` e os retratos de build em `Library/CompressorExclusions`, e ambos podem ser apagados.

## Licença

Uso pessoal e não comercial, incluindo a proibição de vendas e doações, como explica o [LICENSE.md](LICENSE.md).
