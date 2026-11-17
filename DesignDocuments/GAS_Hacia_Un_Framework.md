# El GAS hacia un framework reutilizable

Análisis del 17 de noviembre de 2026, para usar la misma arquitectura en un proyecto de game jam e ir anotando
mejoras para Mercenaries. **No se cambió código:** es la lista de qué cortar y qué le falta, medida sobre el código real.

**Qué se midió:** los 41 archivos del núcleo (`Assets/Scripts/GAS` y `GAS/Entities`, 80 tipos propios) y qué tipos
del juego, definidos FUERA de esa carpeta, usan. Los comentarios no cuentan, solo el código.

---

## 1. Lo que ya está bien (no hay que tocarlo)

- **Funciona sin red para la lógica.** `GameplayAbility.IsServer` devuelve true si el personaje no tiene
  `NetworkAbilitySystemComponent` ("sin red: siempre servidor"). Un juego de un jugador puede usar ASC, habilidades y
  efectos tal cual.
- **Las herramientas del Inspector viajan con él.** `Label`, `Section`, `ShowIf` y las manijas viven en
  `GAS/AbilityAuthoring.cs` y `AbilityHandles.cs`, dentro del núcleo.
- **Dirigido por datos:** clases, habilidades, efectos y atributos son assets. Eso es lo más valioso para una jam:
  se balancea sin compilar.
- **El camino único del daño con eventos** (`OnDamageDone`, `OnHealedAlly`, `OnDealtDamage`, `OnKilledTarget`…) ya
  permite colgar sistemas sin tocar el núcleo. Es el patrón a extender.

---

## 2. Dependencias a cortar (código)

Ordenadas por cuánto pesan. "Refs" = veces que aparece en el código del núcleo.

| # | Depende de | Dónde (refs) | Cómo cortarlo | Esfuerzo |
|---|---|---|---|---|
| 1 | **`NetworkAbilitySystemComponent`** (la capa de red) | GameplayAbility (26), ASC (8), Entity_Companion (4), Entity_ShieldBarrier (2), Entity_Summon (2), GC_Projectile (2) | Una interfaz chica en el núcleo (por ejemplo `IAbilityPresenter`: reproducir VFX y animaciones, números flotantes, el parón) que la capa de red implementa. Sin red, una implementación local que hace todo en el momento. | **Alto**: es casi toda la parte visual de `GameplayAbility`. |
| 2 | **FishNet** en las entidades | `Entity_Summon`, `Entity_Companion`, `Entity_Trap`, `Entity_IllusoryBlade`, `GC_Projectile` son `NetworkBehaviour`; `Entity_Summon.Spawn` y `GA_ProjectileShoot` llaman a `InstanceFinder.ServerManager.Spawn` | Separar la lógica de la entidad (MonoBehaviour) de su sincronización (un componente aparte). `Spawn` pregunta por la interfaz del punto 1 en lugar de llamar a FishNet. | **Alto**. |
| 3 | **`PlayerController`** | GameplayAbility (10: `PredictOwnerVisuals(PlayerController)`, eventos de animación, mira), GC_Projectile (2), Entity_Companion (2), Entity_Trap (1), Summon_Zone (1) | Interfaz `IAbilityOwner` (desde dónde apunta, hacia dónde mira, reproducir una animación, si está en el piso). `PlayerController` y los NPC la implementan. | Medio. |
| 4 | **`SfxCue`** (el sonido) | GameplayAbility (2), GameplayEffect (1) | Llevar el módulo de audio junto con el GAS (es chico) o un campo genérico `AudioClip` con una interfaz para reproducir. | Bajo. |
| 5 | **`Loc`** (los idiomas) | CharacterClassDefinition (2), EClassRole (4), GameplayAbility (2) | Llevar `Loc` junto con el GAS como "módulo común", o una interfaz que devuelva el texto. En una jam de un idioma: `DisplayName` devuelve el campo directo. | Bajo. |
| 6 | **`ECombatNumber`** (los números flotantes) | ASC (8) | Ya es casi un evento: que el ASC lance `OnCombatNumber(cantidad, tipo)` y la UI lo escuche. | Bajo. |
| 7 | **`FirstStrikeCritModifier`** (una pasiva concreta) | ASC (3) | El núcleo no debería conocer una pasiva: que se registre como `IDamageModifier`, como las demás. | Bajo. |
| 8 | **Contenido del juego dentro del núcleo** | `CharacterClassDefinition` → `WallMovementProfile`; `Entity_Companion` → `CompanionManager`; `Entity_ShieldBarrier` → `UpperBodyAim`; `Entity_Summon` → `MercUIFactory`; `GC_Projectile` → `GA_ProjectileShoot` | Mover esas entidades y la definición de clase a una capa "del juego", o invertir la referencia (el juego le avisa al núcleo, no al revés). | Bajo–medio. |
| 9 | **Capas fijas en el código** | `1 << 7` (Character) y `1 << 2` en Entity_Companion y Entity_Summon | Un asset de configuración del GAS (`GasSettings`: capa de personajes, máscara de piso). | Bajo. |

**Cómo empezar el día que se haga:** darle al núcleo su propio `.asmdef` (por ejemplo `Gas.Core`). El compilador
marca de un golpe cada dependencia de esta tabla, y desde ahí no se pueden colar nuevas.

---

## 3. Lo que le falta para otros TIPOS de juego (diseño, no solo dependencias)

1. **Los tags y los atributos son enums del núcleo con cosas de Mercenaries.** `EGameplayTag` tiene 96 valores (70
   son `Status_…`); `EAttributeType` incluye maná, energía, experiencia, nivel, vulnerabilidad, resistencia al control…
   Otro juego tendría que editar el núcleo. Opciones: un archivo de enums POR PROYECTO fuera del núcleo (sigue la regla
   de "solo al final"), o tags como assets (más trabajo, más flexible; así lo hace Unreal, con tags jerárquicos).
2. **Las reglas de daño están metidas en el ASC.** Físico/mágico según el atributo, defensa, vulnerabilidad,
   resistencia, escudo, crítico, por la espalda, robo de vida, resistencia al control, "daño que cura"… todo vive en
   `ExecuteInstantEffect`. Lo reutilizable sería un **cálculo enchufable** (como el `ExecutionCalculation` de Unreal):
   el núcleo aplica modificadores y deja que un asset del juego decida las reglas.
3. **La definitiva está en el núcleo.** `GetUltimateCharge` busca `Ability_Cooldown_Ultimate`, y la carga por rol vive
   en `PlayerController`. Es un concepto del juego: debería ser una capa encima.
4. **Supone un personaje 3D en tercera persona con Animator.** Clips de animación, sockets de armas, la mira de la
   cámara, física 3D (`OverlapSphere`, barridos), NavMesh en los compañeros. Para una jam **3D** sirve tal cual; para
   una **2D** habría que hacer las variantes con física 2D.
5. **`CharacterClassDefinition` mezcla el kit con la presentación** (modelo, armas, animaciones, paredes). Separar
   "qué sabe hacer" de "cómo se ve" lo hace útil para juegos sin clases.
6. **Archivos muy grandes:** el ASC tiene 2 095 líneas y `GameplayAbility` 2 447. Partirlos en `partial class` por
   tema (atributos, efectos, tags, cooldowns, el camino del daño; activación, canalizar, efectos, visuales) facilita
   leerlos y llevarse solo una parte.

---

## 4. Para la game jam (sin cortar nada todavía)

- **Qué copiar:** `Scripts/GAS` completo (con `Entities`), el inspector de habilidades (`Scripts/Editor`), `Loc` y el
  audio (`SfxCue`, `AudioManager`), y las habilidades genéricas de `GameplayAbilities/GenericsAbilities` que se usen.
  Dejar el paquete FishNet instalado: el código lo nombra y no compila sin él.
- **Un jugador, y la trampa de las invocaciones y los proyectiles:** la lógica funciona sin red, pero
  `Entity_Summon.Spawn` y `GA_ProjectileShoot` llaman a `InstanceFinder.ServerManager.Spawn`. Sin un `NetworkManager`
  andando eso falla. Lo más rápido: poner el `NetworkManager` y **arrancar como host solo al iniciar** (en local, sin
  red). Lo correcto (punto 2): que `Spawn` no dependa de FishNet.
- **Tags y atributos:** copiar los enums y BORRAR lo que no se use al empezar (en un proyecto nuevo no hay assets
  viejos que romper); a partir de ahí, solo agregar al final.
- **Qué anotar durante la jam** (para volver con mejoras): cada vez que haya que tocar el núcleo para algo del juego
  nuevo, es una dependencia que falta cortar o un punto de extensión que falta. Anotarlo con el archivo y el porqué.

---

## 5. Copiar lo de hoy a otro proyecto (el camino rápido)

**El pegamento:** el núcleo nombra a `PlayerController` (5 497 líneas) y a `NetworkAbilitySystemComponent` (2 581), así
que sin ellos no compila. Además, el jugador activa sus habilidades por la red (`NetASC.ServerActivateAbility`, con la
mira, el canalizar y lo que se mantiene); los NPC, en cambio, usan el camino directo sin red:
`asc.GrantAbility(plantilla)` → `habilidad.CanActivate()` → `habilidad.Activate()` (ver `MercEnemyAI`).

**Pasos:**
1. Proyecto nuevo con los paquetes: Input System, AI Navigation (los compañeros usan NavMesh), URP (materiales y VFX) y
   TextMeshPro. FishNet viene dentro de `Assets/AssetsExtra/FishNet`.
2. Desde Mercenaries: **Assets ▸ Export Package** con estas carpetas (un `.unitypackage` conserva los GUID, así que las
   referencias entre assets no se rompen):
   - `Scripts/GAS` (con `Entities`).
   - De `Scripts/Editor`: `AbilityEffectDrawer`, `AbilityPreviewEditor`, `AbilityVisualDrawer`,
     `AttributeSetDefinitionEditor`, `CharacterClassDefinitionEditor`, `GameplayAbilityEditor`, `GameplayEffectEditor`,
     `LabeledComponentEditor`, `LabeledFieldsDrawer`, `ModifierDrawer`, `SectionedInspector`, `WallMovementProfileEditor`
     y `MercRegistryTools` (los registros).
   - `Scripts/Localization`, `Scripts/Audio`, `GameplayAbilities/GenericsAbilities`, `AssetsExtra/FishNet`.
   - Plantillas: `Prefabs/UI/Nameplate`, `Prefabs/Summons/Summon_Base` y `SummonZone_Base`, el proyectil base.
   - Los dos registros de `Resources` (vaciarlos y volver a llenarlos con la herramienta).
3. Importar y **compilar**: la lista de errores es exactamente lo que falta.
4. Para cada falta, una de dos:
   - **Traerlo y recortarlo** (`PlayerController`, `NetworkAbilitySystemComponent`): lo más rápido si el juego nuevo
     es 3D en tercera persona, parecido a Mercenaries.
   - **Una versión mínima** con solo los miembros que el núcleo pide (el compilador dice cuáles): un control chico que
     otorga las habilidades y las activa por el camino de los NPC. Sirve para un juego de un jugador.
5. Recortar los enums (`EGameplayTag`, `EAttributeType`) a lo que use el juego nuevo.
6. Escena de prueba: un personaje con ASC, un `AttributeSetDefinition` y una habilidad genérica.

**Cuánto lleva:** medio día a un día trayendo los dos archivos grandes; uno o dos días con versiones mínimas.

**El riesgo de copiar y pegar:** quedan dos copias que se separan. Un arreglo en una no llega a la otra.

---

## 6. Un GAS base para todos los juegos (un "estándar" propio)

**Es posible, y es como lo hacen los estudios:** el GAS como **paquete propio** (UPM) en su repositorio de git. Cada
juego lo agrega desde el Package Manager por la URL; las mejoras salen como versiones (1.0, 1.1…) y cada juego elige
cuándo actualizar. Así un arreglo hecho para un juego llega a todos.

**Forma del paquete:**
- `Core`: ASC, habilidades, efectos, atributos, tags, el camino del daño y sus eventos. **Sin** dependencias del juego
  ni de la red.
- Módulos opcionales: `Net.FishNet` (la capa de red), `Presentation` (VFX, animación, sonido), `UI` (barras, íconos,
  números), `Editor` (el inspector).
- `Samples`: un personaje y dos o tres habilidades de ejemplo.
- Los tags, los atributos y las reglas de daño los define CADA juego (puntos 3.1 y 3.2).

**Qué tan complicado (estimado, a tiempo parcial):**

| Etapa | Qué | Esfuerzo |
|---|---|---|
| 1 | `.asmdef` del núcleo y cortar las dependencias chicas (puntos 2.4 a 2.9) | 2–4 días |
| 2 | Interfaces para la red y para el dueño (puntos 2.1 a 2.3) | 1–2 semanas |
| 3 | Tags y atributos por proyecto; cálculo de daño enchufable | 1–2 semanas |
| 4 | Pasarlo a paquete y que Mercenaries lo use desde ahí (moviendo los archivos CON su `.meta`, para no perder los GUID de los scripts) | ~1 semana |

En total, más o menos un mes y medio repartido.

**Lo que se recomienda:** no hacer "el universal" de entrada, porque es fácil construir flexibilidad que ningún juego
usa. Mejor **sacarlo de a poco con juegos reales**: la jam es el segundo juego que lo usa; se separa solo lo que los dos
necesitaron. Con un tercer juego, la forma ya está probada (la "regla de tres"). Las etapas 1 y 2 también mejoran
Mercenaries, así que convienen después del showcase aunque nunca se haga el paquete.
