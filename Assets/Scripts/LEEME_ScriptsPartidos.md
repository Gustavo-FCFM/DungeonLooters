# Los scripts partidos: cómo encontrar las cosas

Algunos scripts crecieron hasta tener 2 000, 3 000 y hasta 5 500 líneas. Ahí ya era difícil encontrar algo, así que
se partieron en varios archivos más chicos, uno por tema. **El juego funciona exactamente igual**: para Unity
y para C# siguen siendo una sola clase. Solo cambió dónde está escrito cada pedazo.

## La idea en un ejemplo

Antes, todo `PlayerController` estaba en un solo archivo enorme. Ahora:

```
Scripts/Player/
├── PlayerController.cs              ← el archivo PRINCIPAL (el que va en el prefab)
└── PlayerController/                ← una carpeta con el mismo nombre
    ├── PlayerController.Movement.cs     caminar y saltar
    ├── PlayerController.Death.cs        morir y reaparecer
    ├── PlayerController.Input.cs        los botones y las habilidades
    └── …                                (una parte por tema)
```

Cada archivo empieza con `public partial class PlayerController`. La palabra **`partial`** le dice a C#:
"esta clase sigue en otros archivos, júntalos todos". Por eso, aunque estén separados, una parte puede usar
variables y funciones de otra como si estuvieran en el mismo archivo.

## Qué hay en el archivo principal

- **Todos los campos del Inspector**, los que ves y ajustas en Unity.
- Los eventos, los tipos (`enum`, `struct`) y el estado.
- **Una cabecera al principio** que lista cada parte y qué tiene. Si no sabes dónde está algo, abre el
  principal y lee esa lista.

En las partes están las funciones, agrupadas por tema.

## Las 3 reglas

1. **Un campo nuevo del Inspector va SIEMPRE en el archivo principal.** Unity guarda los campos en el orden en que
   están escritos; si quedan repartidos entre archivos, ese orden puede cambiar y desordenarte el Inspector.
2. **El archivo principal no se renombra nunca.** Tiene que llamarse exactamente como la clase
   (`PlayerController.cs`). Si le cambias el nombre, los prefabs dejan de encontrarlo y aparece
   "Missing Script".
3. **Algo nuevo va en la parte de su tema.** Si es un tema nuevo y grande, se hace una parte nueva
   (`PlayerController.Algo.cs` dentro de su carpeta) y se anota en la cabecera del principal.

## Dónde está cada cosa

### `PlayerController`: el jugador (`Scripts/Player/`)

| Si quieres ver o cambiar…                                 | Abre                          |
|-----------------------------------------------------------|-------------------------------|
| Cómo arranca en red y el Update del jugador               | `PlayerController.Lifecycle`  |
| Caminar, saltar, el impulso de las habilidades            | `PlayerController.Movement`   |
| Correr por las paredes y el vuelo libre                   | `PlayerController.Walls`      |
| Los botones y cómo se activan las habilidades             | `PlayerController.Input`      |
| Las habilidades que se mantienen apretadas                | `PlayerController.Hold`       |
| Morir, reaparecer, el registro de bajas                   | `PlayerController.Death`      |
| La clase y el equipamiento                                | `PlayerController.Class`      |
| La carga de la definitiva                                 | `PlayerController.Ultimate`   |
| Animaciones, torso, ragdoll                               | `PlayerController.Animation`  |
| El botón Desatascarse                                     | `PlayerController.Unstuck`    |
| La reacción al recibir un golpe y los pasos               | `PlayerController.HitReaction`|
| El HUD y el apuntado                                      | `PlayerController.Aim`        |
| El giro tipo molinete                                     | `PlayerController.Spin`       |

### `AbilitySystemComponent`: vida, daño y efectos (`Scripts/GAS/`)

| Si quieres ver o cambiar…                                 | Abre                                    |
|-----------------------------------------------------------|-----------------------------------------|
| Los atributos (vida, ataque…)                             | `AbilitySystemComponent.Attributes`     |
| Aplicar efectos, empujes, aturdir, efectos con duración   | `AbilitySystemComponent.Effects`        |
| Cómo se calcula el daño, robo de vida, ejecutar           | `AbilitySystemComponent.Damage`         |
| Defensas, críticos, bloqueo, daño por la espalda          | `AbilitySystemComponent.DamageModifiers`|
| Quitar efectos, limpiar, inmunidades                      | `AbilitySystemComponent.Cleanse`        |
| Habilidades otorgadas y cooldowns                         | `AbilitySystemComponent.Abilities`      |
| Morir, experiencia y nivel                                | `AbilitySystemComponent.Life`           |
| Quién le pegó o lo curó (el registro)                     | `AbilitySystemComponent.CombatLog`      |
| Los eventos de juego y las Reacciones                     | `AbilitySystemComponent.Events`         |

### `GameplayAbility`: la base de todas las habilidades (`Scripts/GAS/`)

| Si quieres ver o cambiar…                                 | Abre                          |
|-----------------------------------------------------------|-------------------------------|
| Activarse, cobrar el costo, terminar, el cooldown         | `GameplayAbility.Lifecycle`   |
| En qué momento de la animación pega                       | `GameplayAbility.HitTiming`   |
| Canalizar y su barra                                      | `GameplayAbility.Cast`        |
| Las cargas                                                | `GameplayAbility.Charges`     |
| Los ecos (Proyección arcana)                              | `GameplayAbility.Echoes`      |
| Aplicar la lista de efectos de la habilidad               | `GameplayAbility.Effects`     |
| Acumulaciones                                             | `GameplayAbility.Stacks`      |
| Dirección, alcance y a quién apunta                       | `GameplayAbility.Targeting`   |
| Los efectos visuales (VFX)                                | `GameplayAbility.Visuals`     |
| Lo que pregunta el cliente (animación, cámara, ícono)     | `GameplayAbility.ClientHooks` |
| Pasar habilidades viejas al formato nuevo                 | `GameplayAbility.Legacy`      |
| Los dibujos de ayuda en la escena (gizmos)                | `GameplayAbility.Gizmos`      |
| Las esperas y los cortes dentro de una habilidad          | `GameplayAbility.Tasks`       |

### `NetworkAbilitySystemComponent`: lo que viaja por la red (`Scripts/Network/`)

| Si quieres ver o cambiar…                                 | Abre                                       |
|-----------------------------------------------------------|--------------------------------------------|
| Lo que se copia a todos (vida, tags, cooldowns, efectos)  | `NetworkAbilitySystemComponent.Sync`       |
| Activar habilidades por red, combos y su animación        | `NetworkAbilitySystemComponent.Abilities`  |
| Destellos, números de daño, sensación de golpe            | `NetworkAbilitySystemComponent.Feedback`   |
| Mover desde el servidor (dash, empuje, teletransporte)    | `NetworkAbilitySystemComponent.Movement`   |
| Los VFX que ven todos                                     | `NetworkAbilitySystemComponent.Vfx`        |
| Morir, revivir, experiencia                               | `NetworkAbilitySystemComponent.Life`       |

### `BotController`: los bots (`Scripts/GameMode/Mercenaries/`)

| Si quieres ver o cambiar…                                 | Abre                          |
|-----------------------------------------------------------|-------------------------------|
| Adónde decide ir (la carga, el santuario, pelear…)        | `BotController.Decision`      |
| A quién ve, cómo camina y mira                            | `BotController.Senses`        |
| Qué habilidad usa, cómo apunta y la activa                | `BotController.Fight`         |
| El dash, el salto, el vuelo del Mago                      | `BotController.Moves`         |
| La rueda de estados (libro de hechizos del Mago)          | `BotController.Wheel`         |
| Revivir, trampas, marcas, el compañero del Maestro de bestias | `BotController.Special`   |
| Esquivar, salir de zonas de daño, rodear escudos          | `BotController.Instinct`      |

Los **números** de los bots (puntería, reacción, etc.) no se cambian en el código: se ajustan en el prefab
`Resources/BotSettings`.

### `GC_Projectile`: los proyectiles (`Scripts/GAS/Entities/`)

| Si quieres ver o cambiar…                                 | Abre                     |
|-----------------------------------------------------------|--------------------------|
| Cómo detecta el choque, el rebote y la vuelta             | `GC_Projectile.Sweep`    |
| Qué pasa al impactar (daño, VFX)                          | `GC_Projectile.Impact`   |
| El lanzamiento (y la lista que miran los bots)            | `GC_Projectile.Flying`   |
| Potenciarlo o devolverlo con un parry                     | `GC_Projectile.Parry`    |
| El modelo que se ve volando                               | `GC_Projectile.Visuals`  |

### `UI_LobbyPanel`: la sala de espera (`Scripts/UI/`)

| Si quieres ver o cambiar…                                 | Abre                        |
|-----------------------------------------------------------|-----------------------------|
| Lo que el panel le pide a la sala (equipo, clase, bots, Start) | `UI_LobbyPanel.Requests` |
| Cómo se dibujan los lugares, columnas y clases            | `UI_LobbyPanel.Draw`        |
| Cómo se conectan las piezas del prefab al empezar         | `UI_LobbyPanel.Wiring`      |

## Trucos para buscar

- **En Visual Studio / Rider:** clic derecho sobre una función y *Ir a definición* (F12). Te lleva a la parte
  correcta aunque esté en otro archivo.
- **Buscar en todos los archivos:** `Ctrl + Shift + F` y el nombre de la función o variable.
- **En Unity**, en el Inspector todo se ve como antes: un solo componente con todos sus campos.
