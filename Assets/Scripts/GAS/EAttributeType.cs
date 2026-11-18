using UnityEngine;

// ============================================================
// EAttributeType
//
// Lista de todos los atributos numéricos que puede tener un
// personaje (vida, maná, daño, velocidad, etc.). AttributeValue
// guarda un valor actual por cada uno de estos, y Modifier/
// GameplayEffect los usan para saber a qué atributo afectar.
// Agregar un valor acá no rompe nada; solo queda sin uso hasta que
// algún AttributeSetDefinition/Modifier lo referencie.
// ============================================================
public enum EAttributeType
{
    [InspectorName("Ninguno")] None,
    [InspectorName("Vida y recursos/Vida")] Health,      // Vida actual
    [InspectorName("Vida y recursos/Vida máxima")] MaxHealth,   // Vida máxima
    [InspectorName("Vida y recursos/Maná")] Mana,        // Maná actual
    [InspectorName("Vida y recursos/Maná máximo")] MaxMana,     // Maná máximo
    [InspectorName("Vida y recursos/Energía")] Energy,      // Energía actual (sistema de bloqueo/stamina)
    [InspectorName("Vida y recursos/Energía máxima")] MaxEnergy,   // Energía máxima
    [InspectorName("Defensa/Defensa")] Def,         // Defensa — reduce el daño físico recibido
    [InspectorName("Ataque/Ataque")] Attack,      // Ataque — daño físico base
    [InspectorName("Movimiento/Velocidad de movimiento")] MovSpeed,    // Velocidad de movimiento
    [InspectorName("Ataque/Tiempo entre ataques")] AtkSpeed,    // Tiempo entre ataques (a menor valor, ataca más rápido)
    [InspectorName("Ataque/Prob. de crítico")] CritChance,  // Probabilidad de golpe crítico
    [InspectorName("Ataque/Robo de vida")] LifeSteal,   // Porcentaje de robo de vida por el daño infligido
    [InspectorName("Progresión/Experiencia")] Exp,         // Puntos de experiencia acumulados
    [InspectorName("Progresión/Experiencia por nivel")] MaxExp,      // Experiencia necesaria para subir de nivel
    [InspectorName("Progresión/Nivel")] Level,       // Nivel del personaje
    [InspectorName("Vida y recursos/Escudo")] Shield,      // Escudo temporal — absorbe daño antes que la vida
    [InspectorName("Ataque/Daño mágico")] MagicDamage, // Daño mágico base
    // IMPORTANTE: agregar valores nuevos SIEMPRE al final. Los .asset serializan
    // los atributos por su número de enum; insertarlos en el medio correría los
    // índices y rompería las referencias ya guardadas en los assets existentes.
    [InspectorName("Ataque/Daño crítico (×)")] CritDamage,  // Multiplicador de daño crítico (ej: 2 = x2). Lo usa el ataque furtivo por la espalda del Pícaro

    // Defensas del que RECIBE el golpe. Se aplican en ExecuteInstantEffect en este
    // orden: Vulnerabilidad → Resistencia → redondeo → Def → escudo → vida.
    // Ver AbilitySystemComponent.ApplyDefenses.
    [InspectorName("Defensa/Vulnerabilidad")] Vulnerability, // % de daño EXTRA que recibe (0.1 = +10%). Lo suben los debuffs de "marcar" a un objetivo
    [InspectorName("Defensa/Resistencia")] Resistance,    // % de daño que EVITA (0.2 = -20%). Lo sube la pasiva del Paladín sobre sus aliados

    // % que ACORTA la duración de los efectos de control que te aplican (aturdir,
    // enraizar, silenciar). 0.3 = te duran un 30% menos; 0 = duración normal.
    //
    // Acepta NEGATIVOS a propósito, y ahí alarga: -0.5 = te duran un 50% más. Eso es
    // lo que permite que un aura enemiga "reduzca tu resistencia al aturdimiento"
    // (Aura conquistadora del Paladín) con el mismo atributo y sin inventar otro.
    //
    // Tiene tope (ver AbilitySystemComponent.MaxCCResistance): por más que se apilen
    // fuentes nunca llega a inmunidad total — para eso está Status_Unstoppable, que
    // es explícito.
    [InspectorName("Defensa/Resistencia al control")] CCResistance,

    // % EXTRA de curación que RECIBE (0.15 = las curaciones le rinden un 15 % más). Lo
    // aplica ExecuteInstantEffect a toda curación que llegue por un efecto. Lo da el Faro de
    // esperanza del Clérigo de la Luz a los aliados dentro de su aura.
    [InspectorName("Defensa/Curación recibida")] HealingReceived,

    // % EXTRA de alcance de los golpes cuerpo a cuerpo (conos y líneas): 0.5 = llegan un 50 %
    // más lejos. Lo da el Avatar del Guardián al crecer. Sin declarar vale 0 (alcance normal).
    [InspectorName("Ataque/Alcance cuerpo a cuerpo")] MeleeRangeBonus
}
