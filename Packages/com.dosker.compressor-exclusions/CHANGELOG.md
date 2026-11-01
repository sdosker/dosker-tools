# Changelog

Todas as mudanças relevantes desta ferramenta ficam neste arquivo.

O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e as versões seguem o [Versionamento Semântico](https://semver.org/lang/pt-BR/).

Neste projeto, a versão muda assim:

- **MAJOR:** mudança que quebra a configuração salva ou exige refazer as regras.
- **MINOR:** novo recurso compatível com a configuração existente.
- **PATCH:** correção, incluindo adaptações a novas versões do VRCFury ou do SDK.

## [1.2.0] - 2026-11-01

### Adicionado

- Cartão **Previsão do build**, com o próximo build, o que se previa para o último e o resultado real, separando parâmetros que mudaram de uma divergência do solver.
- Antes do primeiro build, a janela mostra o avatar da cena, com a tabela e uma previsão parcial.
- Padrões com `?`, `!` para liberar das regras gerais e `menu:` para usar o caminho no menu.
- Botão **Fazer caber**, que sugere o menor conjunto de parâmetros a liberar.
- Histórico de builds por avatar e plataforma.
- Lote de cada parâmetro comprimido e explicação com todas as regras que valem para ele.
- Avisos de PC e Quest diferentes, quando o solver roda em Quest, de nomes que colidem sem o prefixo do VRCFury e de VRCFury mais novo que o testado.
- Botão para limpar escolhas de parâmetros que não existem mais.
- Custo e estado do último build no tooltip do inspector.
- Conferência do VRCFury ao carregar e relatório por `CompressorReport.Export`.

### Corrigido

- Quando o solver roda em Quest, o retrato não sobrescreve mais o de PC.
- Um build que cabe sem compressão mantém a lista de parâmetros.
- Parâmetros com largura de bits ou codificação especial não entram mais na estimativa, como no VRCFury.
- Em batch mode, o build falha em vez de continuar sem as proteções.
- O build avisa quando a configuração ainda não foi carregada, em vez de usar os valores padrão.

## [1.1.2] - 2026-10-31

### Removido

- Aviso sobre IA neste pacote. Ele fica apenas no README principal e na página do listing.

## [1.1.1] - 2026-10-31

### Adicionado

- Aviso de que o projeto foi criado com inteligência artificial.

## [1.1.0] - 2026-10-31

### Alterado

- Distribuição como pacote VPM `com.dosker.compressor-exclusions`, instalável pelo VRChat Creator Companion.
- Menu movido para `Tools/Dosker Tools/Compressor Exclusions`.
- Namespace e assembly renomeados para `CompressorExclusions`.
- Retratos de build movidos para `Library/CompressorExclusions`.
- Configuração nova criada em `Assets/CompressorExclusions`, fora do pacote, para sobreviver às atualizações. Uma configuração existente em outra pasta continua sendo usada.

## [1.0.0] - 2026-10-31

### Adicionado

- Patch Harmony no solver do VRCFury que mantém parâmetros protegidos fora da compressão, sem editar o pacote.
- Regras por tipo de valor, por controle do menu e por padrão de nome com `*`.
- Escolha individual por parâmetro: Auto, Proteger e Comprimir.
- Janela Compressor Exclusions, com resumo ao vivo, regras, busca, filtros e ação em lote.
- Coluna "Compressão" no inspector de VRC Expression Parameters, sincronizada com a janela e com suporte a Undo.
- Retrato de cada build em `Library/`, com o resultado real do VRCFury.
- Estimativa de bits e atraso sem precisar de build.
- Diálogo antes do build quando o patch não está ativo.
