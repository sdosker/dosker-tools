# Performance Budget

Mostra o ranking do avatar antes do upload. Abra em **Tools > Dosker Tools > Performance Budget**.

## O problema

O avatar da cena não é o avatar enviado. No build, o VRCFury funde armaduras, cria componentes e otimiza, então medir só a cena dá um ranking que pode não ser o real.

## Os três números

| Coluna | De onde vem | Confiança |
|---|---|---|
| **Cena** | Medição ao vivo, antes do VRCFury | Só serve de base |
| **Previsão** | Cena + o que o último build mudou, por categoria | Estimativa |
| **Real** | O avatar processado, ao fim do último build | Exata para aquele build |

Os valores e as notas vêm do próprio VRChat SDK, então seguem os limites da versão instalada.

## Como usar

1. Abra a janela e clique em **Medir**.
2. Faça um **Build & Test**. A partir daí, a **Previsão** considera o que o build faz, e a coluna **Real** é preenchida.
3. Ajuste o avatar e meça de novo. A previsão soma suas mudanças ao efeito do último build.

Use as abas **PC** e **Quest** para ver cada plataforma. Cada uma precisa do próprio build, porque as notas e o resultado do VRCFury podem ser diferentes.

## Limites

- Sem build naquela plataforma, a previsão é igual à cena, e a janela avisa.
- A janela avisa quando componentes do VRCFury foram adicionados, removidos ou movidos desde o último build. Mudanças dentro de um componente não são detectadas, e a previsão pode errar até o próximo build.
- A previsão supõe que o build mude o avatar como da última vez. Um toggle novo que funde outra malha, por exemplo, só entra depois de um build.
- O tamanho de download e o uncompressed não entram, porque só existem depois do upload.

## Requisitos

Unity 2022.3 com VRChat Avatars SDK. O VRCFury é opcional: sem ele, Previsão e Cena tendem a coincidir.

## Instalar

Adicione o listing do [Dosker Tools](https://sdosker.github.io/dosker-tools/) ao VRChat Creator Companion e instale este pacote no projeto.

## Desinstalar

Remova o pacote pelo VCC. Nada fica no avatar. As medições ficam em `Library/PerformanceBudget` e podem ser apagadas.
