# CursePlus

Aplicativo desktop para **Minecraft no Windows**, complementar ao CurseForge. Compartilha o estado de um perfil, cria listas quick add e prepara restaurações no mesmo perfil ou em um novo.

## Executar

Baixe o ZIP em [Versões no GitHub](https://github.com/gasharaujo/curseplus/releases), extraia a pasta e abra `CursePlus.exe`. A publicação portátil inclui o .NET e não exige instalação nem administrador. Em uma compilação local, o executável fica em `dist/CursePlus/CursePlus.exe`.

Em desenvolvimento, execute `./build.ps1`; use `./build.ps1 -Publish` para gerar o executável portátil e o ZIP. É necessário o SDK .NET 10.0.402 ou um patch compatível. O script usa o SDK local em `.tools/dotnet` quando disponível. A primeira restauração obtém o FlaUI.UIA3 e suas dependências do NuGet.

## Usar

1. Selecione a instância. Se a pasta padrão não for a sua, configure a pasta `Instances` e o executável do CurseForge em **Configurações**.
2. Use **Salvar ponto** para guardar o estado atual. Em perfis próprios, o primeiro ponto também estabelece o original; alterações anteriores a esse momento não podem ser reconstruídas.
3. Em modpacks publicados, **Definir original** instala pelo CurseForge uma referência limpa da versão exata do pack. Ela aparece como outro perfil no CurseForge e é mantida para consultas. O perfil atual permanece como está.
4. Use **Exportar alterações** depois de definir o original. O padrão é um **`.curseplus.json` compacto**, com somente mods adicionados, removidos, atualizados ou ativados/desativados, mais as referências de Minecraft, loader e pack. Mods inalterados e a lista completa de arquivos originais ficam fora da exportação.
   Quando existem configs, scripts ou JARs locais/modificados diferentes do original, o resultado é um **`.curseplus.zip`**, com `changes.json`, `summary.txt` e somente esses anexos em `files/`, com caminhos legíveis. Os JARs gerenciados normais continuam sendo apenas referências por ID. Arquivos removidos são registrados por caminho, sem conteúdo anexado.
   A exportação funciona com o CurseForge fechado. **Sem original salvo, ela é interrompida com uma orientação**, sem copiar todas as pastas como alternativa. Backups `.bak`, `.backup`, `.old`, temporários `.tmp`, `.temp`, logs `.log`, arquivos terminados em `~`, arquivos auxiliares do sistema e caches/logs/exportações do KubeJS ficam fora da captura. Assets usados pelo pack continuam no escopo, mas só são anexados quando adicionados ou alterados.
   A captura considera o conteúdo real da pasta: mods que ainda constam dos metadados, mas cujos arquivos foram removidos, ficam de fora e são relacionados no resumo. Aguarde instalações/downloads em andamento terminarem antes de capturar um perfil.
5. Em **Sincronizar**, abra o JSON ou ZIP, selecione o destino e gere a prévia. O original salvo recompõe o estado esperado em memória, sem gerar uma cópia completa do pack. Se faltar a referência de um modpack publicado, a prévia a obtém por uma instalação limpa pelo CurseForge; referências salvas são reutilizadas. Perfis próprios exigem o mesmo original e vínculo de origem no destino. A assinatura do original é conferida antes da aplicação.
   **Preferir A** reproduz o conteúdo sincronizado da origem, incluindo remoções. **Mesclar** conserva itens exclusivos do destino; marque **MANTER B** nos conflitos que deseja conservar. A decisão para arquivos escolhe uma cópia completa.
6. **Aplicar no destino** salva primeiro um ponto de restauração, executa as ações e verifica o resultado. A versão do pack é alinhada antes das personalizações. Packs diferentes são bloqueados.
7. Para perfis próprios, use **Vincular perfil próprio** antes de sincronizar outra instalação da mesma origem. IDs de modpack iguais a zero não são tratados como a mesma origem.
8. Em **Quick add**, crie uma lista selecionando mods na aba Instância. Renomeie, remova itens, importe/exporte a lista e aplique ao perfil escolhido. O ID do projeto permanece fixo; o CurseForge resolve a versão compatível. Um mod já instalado é conservado.
9. **Restaurar** prepara uma prévia do ponto escolhido ou do original, no perfil atual ou em um novo perfil. Revise e aplique as personalizações após a criação do novo perfil.

## Regra de integração

Instalação, remoção, versões, ativação, modloader e criação de perfis usam a interface real do CurseForge, por UI Automation 3 do Windows. Instalações com IDs exatos usam o protocolo nativo `curseforge://install`. O CursePlus lê `minecraftinstance.json`, mas **não o modifica**, e não altera bancos, caches ou arquivos internos do CurseForge.

Somente `config/`, `defaultconfigs/`, `scripts/`, `kubejs/` e JARs locais/modificados em `mods/` podem receber escrita direta quando a operação por arquivo não está disponível no CurseForge. Caches e logs do KubeJS ficam de fora. JARs gerenciados normais só são alterados pelo CurseForge. Os caminhos são relativos e os conteúdos são verificados por SHA-256. Links de arquivo/diretório e caminhos fora da instância são rejeitados.

Mundos, resource packs, shaders, contas, logs e configurações do launcher ficam fora da sincronização. Mantenha o jogo do perfil de destino fechado. Durante uma aplicação, o CursePlus traz o CurseForge à frente e usa mouse e teclado; aguarde a conclusão. O adaptador exige nomes de perfil únicos, recusa seletores ambíguos e confere o controle sob o cursor antes dos cliques; não usa coordenadas fixas.

Trocar a família do modloader ou a versão do Minecraft de um perfil próprio exige um novo perfil. A mudança nativa de versão de um modpack pode alinhar esses parâmetros. Arquivos/versões indisponíveis e telas não reconhecidas interrompem a operação. Não há substituição automática por outra versão nem instalação externa de mods.

## Estado de validação

**Beta com exportação leve atualizada em 24/10/2026; 77 testes automatizados aprovados.** Em teste controlado com 429 mods e 2.797 arquivos originais inalterados, adicionar um único mod gerou **653 bytes de JSON**, sem leitura ou cópia dos assets originais. Um caso salvo de validação com dois mods alterados e uma configuração anexada caiu de **10.108 para 1.103 bytes**.

Em 23/10/2026, os fluxos nativos passaram no CurseForge 1.324.0.41180 para Windows, interface em inglês: criação/importação, instalação por arquivo exato, quick add compatível, recusa de incompatibilidade, ativação, remoção, troca de versão Forge, mudança de versão do pack e restauração no mesmo e em novo perfil. A alteração do formato de exportação foi testada sem controlar a tela, com o adaptador nativo simulado. Consulte `VALIDATION.md` para os casos e limites.

Pacotes antigos completos continuam sendo lidos. O formato leve usa a versão 2: atualize o CursePlus nas duas pontas antes de compartilhá-lo.

Quick add usa `curseforge://install` com o ID do projeto e sem fixar arquivo: o CurseForge escolhe a versão compatível no seu seletor de perfis. O ID instalado é conferido nos metadados; projetos incompatíveis são informados sem instalação.

Os nomes alternativos de controles em português ainda não passaram por validação completa. Fabric, NeoForge e Quilt não tiveram instalação real testada nesta sessão. Mudanças de layout do CurseForge ou versões indisponíveis podem interromper uma operação. Criar um original limpo quando a mesma versão já está instalada depende de **Change Version → Select version → Update to a separate modpack profile**; packs sem esse fluxo disponível não têm um caminho alternativo externo. Referências limpas podem aparecer como perfis adicionais na biblioteca, inclusive ao trocar a versão do pack.

## Recuperação e dados

Configurações, listas e pontos ficam em `%LOCALAPPDATA%/CursePlus`, separados das instâncias. As referências originais ficam no armazenamento local; não são anexadas às exportações. Pontos e backups também guardam só diferenças quando já existe uma referência original; sem ela, o ponto completo continua disponível para recuperação. Operações ficam registradas em `operations/`, com o pacote de origem e o ponto anterior. **Revisar / retomar** recalcula a prévia a partir do estado atual e preserva o estado desejado da mescla anterior. Interromper impede as próximas etapas; a aplicação de mudanças aguarda a etapa nativa corrente terminar. Downloads já iniciados pelo CurseForge podem continuar; fechar o CurseForge durante uma instalação pode deixá-la incompleta.

Pontos não incluem cópias dos JARs normais. Restaurá-los depende de essas versões continuarem disponíveis no CurseForge.

## Estrutura e testes

- `src/CursePlus.Core`: modelos, leitura, comparação, arquivos, armazenamento e executor.
- `src/CursePlus.Desktop`: interface WPF e adaptador UIA3.
- `tests/CursePlus.Tests`: testes executáveis determinísticos, sem alteração de instâncias reais.

Execute `./build.ps1` para compilar e rodar os testes. Para diagnóstico somente de leitura: `CursePlus.exe --inspect-ui caminho.json`. Para renderização isolada das telas: `CursePlus.exe --smoke pasta-de-saida`.

O projeto não é afiliado ao CurseForge/Overwolf.
