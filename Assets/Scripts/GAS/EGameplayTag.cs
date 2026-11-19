using UnityEngine;

// ============================================================
// EGameplayTag
//
// Etiquetas de estado que un AbilitySystemComponent puede tener
// (aturdido, en cooldown, envenenado, etc.). Los GameplayEffect las
// otorgan/quitan mientras están activos, y CanActivate()/HasTag()
// las usan para bloquear acciones o consultar estado. También se
// usan como "identidad" de un efecto (ver GrantedTags[0] en
// GameplayAbility.CanActivate y NetworkAbilitySystemComponent) para
// poder referenciarlo sin mandar el ScriptableObject por red.
// ============================================================
public enum EGameplayTag
{
    [InspectorName("(ninguno)")] None,

    // --- ESTADOS DE CONTROL (CC) ---
    [InspectorName("Control/Aturdido")] State_Stunned,   // Bloquea todo (movimiento y habilidades)
    [InspectorName("Control/Enraizado")] State_Rooted,    // Bloquea solo el movimiento
    [InspectorName("Control/Silenciado")] State_Silenced,  // Bloquea habilidades (movimiento permitido)
    [InspectorName("Estado/Muerto")] State_Dead,      // Estado de muerte

    // --- COOLDOWNS (uno por slot de habilidad) ---
    [InspectorName("Cooldowns/Global")] Ability_Cooldown_Global,
    [InspectorName("Cooldowns/Cuerpo a cuerpo")] Ability_Cooldown_Melee,
    [InspectorName("Cooldowns/Definitiva")] Ability_Cooldown_Ultimate,
    [InspectorName("Cooldowns/Habilidad 0")] Ability_Cooldown_0,
    [InspectorName("Cooldowns/A distancia")] Ability_Cooldown_Ranged,
    [InspectorName("Cooldowns/Especial")] Ability_Cooldown_Special,
    [InspectorName("Cooldowns/Extra")] Ability_Cooldown_Extra,
    [InspectorName("Cooldowns/Movimiento")] Ability_Cooldown_Movement,

    // --- EFECTOS DE ESTADO (BUFFS/DEBUFFS) ---
    [InspectorName("Daño con el tiempo/Veneno")] Status_Poison,
    [InspectorName("Daño con el tiempo/Quemadura")] Status_Burning,
    [InspectorName("Control/Lento")] Status_Slow,
    [InspectorName("Estado/Más daño")] Status_Buff_Damage,  // Ej: Grito de guerra
    [InspectorName("Estado/Más velocidad")] Status_Buff_Speed,   // Ej: Sprint
    [InspectorName("Estado/Inmune al daño")] Status_Immunity,     // Ej: Invencible
    [InspectorName("Bárbaro/Furia")] Status_Rage,
    [InspectorName("Bárbaro/Frenesí")] Status_Frenzy,
    [InspectorName("Estado/Inmortal (queda en 1)")] Status_Immortal,
    [InspectorName("Bárbaro/Chamán: buff del oso")] Status_Buff_Bear,
    [InspectorName("Bárbaro/Chamán: buff del lobo")] Status_Buff_Wolf,
    [InspectorName("Bárbaro/Chamán: buff del águila")] Status_Buff_Eagle,
    [InspectorName("Bárbaro/Chamán: buff del tigre")] Status_Buff_Tiger,

    // --- COOLDOWNS DE TÓTEMS ---
    [InspectorName("Cooldowns/Tótem del oso")] Totem_Cooldown_Bear,
    [InspectorName("Cooldowns/Tótem del lobo")] Totem_Cooldown_Wolf,
    [InspectorName("Cooldowns/Tótem del águila")] Totem_Cooldown_Eagle,
    [InspectorName("Cooldowns/Tótem del tigre")] Totem_Cooldown_Tiger,

    // IMPORTANTE: agregar tags nuevos SIEMPRE al final. Los .asset serializan los
    // tags por su NÚMERO de enum; insertarlos en el medio corre los índices y
    // rompe las referencias ya guardadas (ej. Status_Immortal, Rage, tótems).
    [InspectorName("Daño con el tiempo/Heridas")] Status_Wound,      // Heridas del Pícaro — daño con el tiempo, apilable (ver GE_Heridas)
    [InspectorName("Obsoletos/Passive_Backstab")] Passive_Backstab,  // OBSOLETO: el backstab ahora es BackstabDamageModifier (pasiva por prefab). No borrar (corre índices).

    // --- ASESINO ---
    [InspectorName("Estado/Invisible")] Status_Invisible,        // Invisible para los ENEMIGOS; vos te ves fantasma (ver PlayerVisibility)
    [InspectorName("Estado/Crítico asegurado")] Status_GuaranteedCrit,   // El próximo golpe es crítico sí o sí; se CONSUME al usarlo (ver ResolveOutgoingDamage)
    [InspectorName("Obsoletos/Passive_FirstStrikeCrit")] Passive_FirstStrikeCrit, // OBSOLETO: el crítico mejorado ahora es FirstStrikeCritModifier (pasiva por prefab). No borrar (corre índices).

    // --- ILUSIONISTA ---
    [InspectorName("Obsoletos/Passive_Illusory_Blades")] Passive_Illusory_Blades,  // OBSOLETO: las cuchillas ahora son IllusoryBladesPassive (pasiva por prefab). No borrar (corre índices).
    [InspectorName("Control/Cegado")] Status_Blinded,          // Cegado ("flashbang"): lo aplica la Copia exacta al enemigo que la golpea (ver Entity_PlayerCopy)

    // --- PIRATA ---
    [InspectorName("Estado/Imparable")] Status_Unstoppable,      // Imparable: limpia los debuffs al otorgarse y bloquea nuevos debuffs con duración (CC/DoT) mientras dura (ver ApplyGameplayEffect)
    [InspectorName("Pícaro/Apostado (Pirata)")] Status_Gambled,          // Apostar: el enemigo sobre el que el Pirata apostó. Recibe más daño DEL PIRATA (ver GamblePassive)

    // --- PALADÍN ---
    [InspectorName("Estado/Bloqueando (escudo arriba)")] Status_Blocking,         // Escudo levantado (GA_ShieldBlock). Dura lo que el jugador mantenga el botón; lo usan la animación de hold y el VFX de la barrera
    [InspectorName("Paladín/Castigo divino cargado")] Status_Divine_Smite,      // Castigo divino cargado: el próximo ataque principal se cambia por el combo cono+estela (ver GA_TagSwitch)
    [InspectorName("Paladín/Aura de protección")] Status_Aura_Protection,  // El personaje está dentro del Aura de protección de un Paladín aliado (marca para VFX; el stat lo da el GE del aura)
    [InspectorName("Paladín/Aura de devoción")] Status_Aura_Devotion,

    // --- PALADÍN · JURAMENTO DE LA VENGANZA ---
    [InspectorName("Paladín/Aura de venganza")] Status_Aura_Vengeance,   // El enemigo está dentro del Aura de venganza (marca para VFX; la Vulnerabilidad la da el GE del aura)
    [InspectorName("Paladín/Enemigo jurado")] Status_Sworn_Enemy,       // Enemigo jurado: recibe más daño y CURA a los aliados que lo golpean (ver GA_SwornEnemy)
    [InspectorName("Paladín/Ángel vengador")] Status_Avenging_Angel,    // Ángel vengador activo: cambia el ataque principal (vía GA_TagSwitch) y habilita los anillos de aura condicionales

    // --- MOVIMIENTO ---
    [InspectorName("Estado/Caída de pluma")] Status_Feather_Fall,      // Caída de pluma: cae más lento y puede volver a impulsarse en el aire sin límite mientras dure, como un aleteo (ver PlayerController.HandleMovementInput). NO es volar libremente — eso sería un Status_Flight aparte, con control vertical propio

    // --- DEFENSIVOS COMPARTIDOS ---
    [InspectorName("Estado/Escudo que cura")] Status_HealShield,       // Mientras dure, lo que el ESCUDO frena se devuelve como vida (Cubrir con escudo de la Conquista, defensa mejorada del Monje). Ver ExecuteInstantEffect
    [InspectorName("Paladín/Invencible (Conquista)")] Status_Invincible_Conqueror, // Invencible de la definitiva del Juramento de la conquista
    [InspectorName("Paladín/Aura de la conquista")] Status_Aura_Conqueror,       // El personaje está dentro del Aura de la Conquista de un Paladín aliado (marca para VFX; el stat lo da el GE del aura)

    // --- MODO MERCENARIOS ---
    [InspectorName("Modo/Lleva el Objetivo")] Status_Carrying_Objective, // Lleva el Objetivo encima: se mueve más lento y su botón de definitiva pasa a SOLTARLO (ver MercObjective)
    [InspectorName("Modo/En su sala segura")] Status_SafeZone,           // Está dentro de la sala segura de SU equipo: vida topeada, inmune al daño, y es el único lugar donde puede cambiar de clase (ver MercTeamBase)

    // --- CANALIZADOS ---
    [InspectorName("Estado/Canalizando")] Status_Channeling,         // Está en medio de una habilidad canalizada (el molinete del bárbaro). Mientras dure, CanActivate bloquea TODA otra habilidad salvo las marcadas con UsableWhileChanneling. Lo pone y lo saca GA_ContinuousAoE con BlockOtherAbilities

    [InspectorName("Control/Desarmado")] State_Disarmed,            // Desarmado: no puede usar las acciones de ARMA (golpear con ella o lanzarla). Lo bloquea el ActivationBlockedTags de cada habilidad de arma (desde el 1 de octubre; antes era solo el ataque básico). Lo da la Zona de verdad del Clérigo del Orden; lo va a reusar el Guerrero

    [InspectorName("Estado/Revelado (los enemigos lo ven)")] State_AlwaysVisible,       // Revelado: aunque tenga Status_Invisible, los ENEMIGOS lo ven igual (modelo, barra de vida, números). Ver ASC.IsHiddenFromEnemies. Lo da el Faro de esperanza del Clérigo de la Luz a los enemigos dentro de su aura

    // --- POSTURAS DEL GUERRERO ---
    [InspectorName("Guerrero/Postura defensiva")] Stance_Defensive,          // Postura defensiva del Guerrero (+armadura). La da GE_StanceDefensive; con ella el Shift es la Carga defensiva
    [InspectorName("Guerrero/Postura ofensiva")] Stance_Offensive,          // Postura ofensiva del Guerrero (+ataque). La da GE_StanceOffensive; con ella el Shift es la Carga ofensiva
    [InspectorName("Guerrero/(grupo de las posturas)")] Status_Stance,             // No lo otorga nadie: es el EffectGroup de las dos posturas (con ReplacesGroup, entrar a una saca la otra)

    // --- MAESTRO DE BATALLA ---
    [InspectorName("Guerrero/Romper límites")] Status_BreakLimits,        // Romper límites activo: Punto débil aplica su marca potenciada (las reacciones de GE_WeakPointPassive). Lo da GE_BreakLimits junto con Status_Unstoppable
    [InspectorName("Guerrero/Punto débil (marcado)")] Status_WeakPoint,          // Marcado por Punto débil (recibe más daño). También es el EffectGroup de las dos marcas, con ReplacesGroup

    // --- COMANDANTE ---
    [InspectorName("Guerrero/Voz de mando")] Status_CommandingVoice,    // Voz de mando activa: prende el anillo de velocidad del aura del Comandante (PaladinAuraPassive con RequiredOwnerTag). Lo da GE_CommandingVoice

    // --- GUARDIÁN ---
    [InspectorName("Guerrero/Avatar")] Status_Avatar,             // Avatar activo: el modelo crece (CharacterClassDefinition.GrowTag) y sus golpes cuerpo a cuerpo llegan más lejos (MeleeRangeBonus). Lo da GE_Avatar junto con Status_Unstoppable

    // --- SISTEMAS COMPARTIDOS ---
    [InspectorName("Estado/Volando")] Status_Flying,             // Vuelo libre: sin gravedad, se mueve en 3D hacia donde mira la cámara, Espacio sube y Ctrl baja; no aterriza mientras dure; el daño o un enraizado lo terminan. Lo da el GE del GA_Flight (ver PlayerController → VUELO LIBRE)

    // --- MONJE ---
    [InspectorName("Monje/Ki preparado")] Status_Ki,                 // Ki preparado: la PRÓXIMA acción (Disparo, Apuntado o Patada) sale en su versión con Ki y lo gasta (GA_TagSwitch / GA_HoldTagSwitch con ConsumeTag). Lo da GE_Ki
    [InspectorName("Monje/Defensa paciente")] Status_PatientDefense,     // Defensa paciente: la barrera en cápsula del Monje (Entity_ShieldBarrier con Omnidirectional) está arriba sin mantener el botón. Lo da GE_PatientDefense
    [InspectorName("Monje/Artes marciales (acumulación)")] Status_MartialArts,        // UNA por acumulación de Artes marciales (GE_MartialArts y sus variantes de subclase, como GE_MartialArtsSharpen del Samurái). Las habilidades que GASTAN acumulaciones las cuentan y las quitan por este tag (sección Acumulaciones del GA)
    [InspectorName("Estado/Esquiva proyectiles")] Status_DodgeProjectiles,   // Esquiva proyectiles: los enemigos lo atraviesan sin hacerle nada, y su barrera no los para (el bloqueo y la Defensa paciente del Shinobi). Ver GC_Projectile
    [InspectorName("Estado/El daño lo cura")] Status_DamageToHeal,       // El daño que recibe lo CURA en vez de dañarlo (los primeros segundos de la Defensa de agua del Maestro elemental). Ver AbilitySystemComponent.ExecuteInstantEffect
    [InspectorName("Monje/Paz mental")] Status_PeaceOfMind,        // Paz mental del Maestro elemental: sus variantes con Ki salen sin gastar el Ki (GA_TagSwitch.TagVariant.AlsoActiveWithTag)

    // --- EXPLORADOR ---
    [InspectorName("Explorador/Ataque rápido (disparo)")] Status_QuickShot,          // UNA por disparo de Ataque rápido que queda: el arco suelta YA la carga máxima y gasta una (GA_ChargedAttack.FullChargeTag). Lo da GE_QuickShot (3 acumulaciones)
    [InspectorName("Explorador/Marca del cazador (marcado)")] Status_HuntersMark,        // Marcado por la Marca del cazador (recibe más daño). El "se lo ve a través de paredes" NO va por tag: lo revela al equipo del que marcó (NetworkAbilitySystemComponent.ServerRevealToTeam)
    [InspectorName("Explorador/Espada en mano")] Status_BladesDrawn,        // Tiene la espada corta en la mano: el arma principal sale de la espalda mientras dure (CharacterClassDefinition.DrawMainHandTag). Lo da GE_BladesDrawn al cortar con el clic derecho del Explorador

    // --- MAESTRO DE BESTIAS ---
    [InspectorName("Estado/Provoca a los NPC")] Status_Taunting,           // Provoca a los NPCs: si cerca no hay otro jugador que su dueño, los fantasmas lo atacan a él (el Oso). Ver MercEnemyAI.FindTarget
    [InspectorName("Explorador/Compañero: lobo")] Status_CompanionWolf,      // El compañero elegido es el Lobo (lo da el GE de compañero activo; con él el cliente sabe cuál dibujar en el HUD y en el botón)
    [InspectorName("Explorador/Compañero: oso")] Status_CompanionBear,      // El compañero elegido es el Oso
    [InspectorName("Explorador/Compañero: búho")] Status_CompanionOwl,       // El compañero elegido es el Búho
    [InspectorName("Cooldowns/Reaparición del lobo")] Cooldown_CompanionWolf,    // El Lobo murió y todavía no puede volver (GE de reaparición del Lobo, en el dueño)
    [InspectorName("Cooldowns/Reaparición del oso")] Cooldown_CompanionBear,    // El Oso murió y todavía no puede volver
    [InspectorName("Explorador/Ira primal")] Status_PrimalFury,         // Ira primal (definitiva del Maestro de bestias): mientras dure, salen los tres compañeros, atacan el doble de rápido y su presencia vale doble. Lo da GE_PrimalFury; lo mira el CompanionManager

    // --- CAZADOR (subclase del Explorador) ---
    [InspectorName("Explorador/Presa (marcado)")] Status_Prey,               // Marcado como Presa por un Cazador: sus golpes le hacen más según el ROL del marcado (Tanque/Daño), o recibe menos curación (Soporte). Lo da GE_Prey; quién lo marcó y su rol los guarda HunterPreyPassive
    [InspectorName("Explorador/Depredador definitivo")] Status_ApexPredator,       // Depredador definitivo (definitiva del Cazador): todos los enemigos son su Presa y su equipo los ve a través de paredes; cada baja la alarga. Lo da GE_ApexPredator; lo mira HunterPreyPassive
    [InspectorName("Explorador/Tácticas defensivas (acumulación)")] Status_DefensiveTactics,   // UNA por acumulación de Tácticas defensivas (pasiva del Cazador): cada golpe recibido suma una (velocidad y resistencia). Lo da GE_DefensiveTactics
    [InspectorName("Explorador/Tácticas listas")] Status_TacticsReady,       // Tácticas defensivas al máximo: la próxima Retirada lo vuelve invisible y las gasta (DefensiveTacticsPassive)

    // --- MAGO ---
    [InspectorName("Mago/Punto arcano")] Status_ArcanePoint,        // UNA por punto arcano (GE_ArcanePoint, tope 5, 15 s): los dejan los Misiles arcanos y cada golpe del Mago le hace +1 por punto (ArcaneVulnerabilityPassive)
    [InspectorName("Mago/Hechizo elegido")] Status_SpellSelected,      // Tiene un hechizo elegido en el libro (lo dan los GE_Spell_*; sin él, el clic izquierdo son los misiles y la Q recarga maná)
    [InspectorName("Mago/Hechizo: fuego")] Status_SpellFire,          // Hechizo elegido: fuego (GE_Spell_Fire)
    [InspectorName("Mago/Hechizo: hielo")] Status_SpellIce,           // Hechizo elegido: hielo (GE_Spell_Ice)
    [InspectorName("Mago/Hechizo: tierra")] Status_SpellEarth,         // Hechizo elegido: tierra (GE_Spell_Earth)
    [InspectorName("Mago/Hechizo: rayo")] Status_SpellLightning,     // Hechizo elegido: rayo (GE_Spell_Lightning)
    [InspectorName("Mago/Tumba helada")] Status_IceTomb,            // Encerrado en la Tumba helada (GE_IceTomb, junto con la inmunidad y el enraizado): sus otras habilidades lo llevan en ActivationBlockedTags, y apretar la Q otra vez lo saca (GA_SelfBuff "Apretar otra vez la termina")
    [InspectorName("Mago/Memoria perfecta")] Status_PerfectMemory,      // La próxima Q del Mago (no la recarga) sale al instante y sin cooldown, y gasta una (la Memoria perfecta de la Evocación; la Sobrecarga da 3). Ver GA_TagSwitch.InstantTag
    [InspectorName("Mago/Vuelo firme")] Status_SteadyFlight,       // Vuelo firme: el daño no corta el vuelo libre (la Sobrecarga de hechizos de la Evocación levita a voluntad). Ver ASC: "Vuelo libre"
    [InspectorName("Mago/Proyectado (Abjuración)")] Status_ArcaneProjection,   // Proyectado por un Abjurador (GE_ArcaneProjection, lo refresca ArcaneProtectionPassive): sus clics izquierdos salen además desde acá. Solo para el ícono y el VFX
    [InspectorName("Mago/Protección máxima")] Status_MaximumProtection,  // La Protección máxima del Abjurador está activa (GE_MaximumProtection, 8 s): proyecta a todos los aliados a 25 m. Ver ArcaneProtectionPassive
    [InspectorName("Mago/Espada mágica")] Status_MagicBlade,         // La Espada mágica del Filo danzante (GE_MagicBlade, 8 s): sus cortes hacen daño mágico (el "Daño mágico con el tag" de GA_ConeAttack / GA_Dash)
    [InspectorName("Mago/Danza defensiva (acumulación)")] Status_DefensiveDance,     // UNA por cada 3 % de la Danza defensiva del Filo danzante (GE_DefensiveDance; las pone y saca DefensiveDancePassive)

    // --- GRUPOS (18 de noviembre de 2026, tags jerárquicos). Nadie los DA: HasTag(grupo) = tiene alguno de sus hijos, y
    // quitar / inmunizar / exigir por un grupo vale para todos sus hijos. Quién es hijo de quién: GameplayTagTree. ---
    [InspectorName("Grupos/Control (todo)")] Group_Control,             // todo lo que limita: el Control duro + desarme, lentitud y ceguera
    [InspectorName("Grupos/Control duro")] Group_HardControl,         // el control DURO (hijo de Group_Control): aturdido, enraizado, silencio. Es el que recorta la Resistencia al control
    [InspectorName("Grupos/Daño con el tiempo")] Group_DamageOverTime,      // daño con el tiempo: veneno, quemadura, heridas
    [InspectorName("Grupos/Posturas")] Group_Stance,              // las posturas (defensiva, ofensiva)
    [InspectorName("Grupos/Hechizo del Mago")] Group_Spell,               // el hechizo elegido del Mago (fuego, hielo, tierra, rayo)
    [InspectorName("Grupos/Compañero activo")] Group_Companion,           // el compañero activo del Señor de las bestias (lobo, oso, búho)
    [InspectorName("Grupos/Buff de tótem")] Group_TotemBuff,           // los buffs de los tótems del Chamán (oso, lobo, águila, tigre)
    [InspectorName("Grupos/Auras del Paladín")] Group_Aura,                // las auras del Paladín (protección, devoción, venganza, conquista)
    [InspectorName("Grupos/Cooldowns (todos)")] Group_Cooldown,            // todos los cooldowns (de habilidades, de tótems, de compañeros)

    // --- 19 de noviembre de 2026 ---
    [InspectorName("Control/Provocado (ataca al que lo provocó)")] Status_Taunted,   // PROVOCADO: los NPC y los bots que lo tienen van por QUIEN se lo puso (la fuente del GE; ver ASC.GetTaunter). Distinto de Status_Taunting (el señuelo del Oso)
}
