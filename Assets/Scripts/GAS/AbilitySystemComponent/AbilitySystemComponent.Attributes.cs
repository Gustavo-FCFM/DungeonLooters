using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent — LOS ATRIBUTOS
//
// Una parte de AbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/AbilitySystemComponent.cs; ahí está también la lista de las partes.
//
// Cargarlos de la definición, leerlos, escribirlos (con sus topes) y recalcularlos con los
// modificadores de los efectos activos.
// ============================================================
public partial class AbilitySystemComponent
{
    // =========================================================
    // ATRIBUTOS
    // =========================================================

    // (Re)carga todos los atributos desde CharacterRoleDefinition. Si el
    // personaje ya tenía nivel/experiencia, los conserva (se llama de
    // nuevo al cambiar de clase). Llamarla resetea buffs/modificadores
    // temporales sobre los atributos recalculados.
    // keepProgress=true (por defecto) conserva nivel/exp si ya los tenía —lo que
    // se quiere al evolucionar a subclase—. En false reinicia a nivel 1 / 0 exp:
    // lo usa el cambio manual de clase desde el menú, que arranca la clase nueva
    // desde cero.
    public void InitializeAttributes(bool keepProgress = true)
    {
        if (CharacterRoleDefinition == null) return;

        float savedLevel = 1, savedExp = 0;
        bool hadProgress = keepProgress && Attributes.ContainsKey(EAttributeType.Level);
        if (hadProgress) { savedLevel = GetAttributeValue(EAttributeType.Level); savedExp = GetAttributeValue(EAttributeType.Exp); }

        // Qué atributos TENÍA antes de vaciar. Hace falta para avisar de los que
        // DESAPARECEN al cambiar de clase — ver el bloque de avisos del final.
        _clearedAttributeKeys.Clear();
        foreach (var pair in Attributes) _clearedAttributeKeys.Add(pair.Key);

        Attributes.Clear();

        // Primero TODO lo que el set define explícitamente.
        foreach (var attrData in CharacterRoleDefinition.InitialAttributes)
            if (!Attributes.ContainsKey(attrData.Attribute))
                Attributes.Add(attrData.Attribute, new AttributeValue(attrData.BaseValue));

        // Recién después derivamos los "Max" de su pool, y SOLO si el set no los trae.
        // Antes se pisaban siempre con el valor de Health/Mana, así que un
        // AttributeSetDefinition que definiera MaxHealth distinto a Health lo veía
        // ignorado en silencio (imposible arrancar con la vida no llena, por ejemplo).
        if (!Attributes.ContainsKey(EAttributeType.MaxHealth) && Attributes.ContainsKey(EAttributeType.Health))
            Attributes[EAttributeType.MaxHealth] = new AttributeValue(Attributes[EAttributeType.Health].BaseValue);

        if (!Attributes.ContainsKey(EAttributeType.MaxMana) && Attributes.ContainsKey(EAttributeType.Mana))
            Attributes[EAttributeType.MaxMana] = new AttributeValue(Attributes[EAttributeType.Mana].BaseValue);

        if (!Attributes.ContainsKey(EAttributeType.Level))  Attributes[EAttributeType.Level]  = new AttributeValue(1);
        if (!Attributes.ContainsKey(EAttributeType.Exp))    Attributes[EAttributeType.Exp]    = new AttributeValue(0);
        if (!Attributes.ContainsKey(EAttributeType.MaxExp)) Attributes[EAttributeType.MaxExp] = new AttributeValue(100);

        if (hadProgress && savedLevel > 1)
        {
            Attributes[EAttributeType.Level].CurrentValue = savedLevel;
            Attributes[EAttributeType.Exp].CurrentValue   = savedExp;
        }

        // Avisar de TODO para que la capa de red lo sincronice: acá los atributos se
        // escriben DIRECTO en el diccionario (no por SetCurrentAttributeValue), así que
        // nadie disparó OnAttributeChangedCallback.
        //
        // Antes se avisaban SOLO los pools (Vida, Maná, Energía), y eso dejaba dos
        // agujeros al cambiar de clase, porque NetStats únicamente se ESCRIBE: no tiene
        // forma de decir "este atributo ya no existe".
        //
        //   · Un stat que la clase NUEVA no declara se quedaba con el valor de la
        //     VIEJA. El caso real: el AttributeSet del Berserker trae LifeSteal 0.3
        //     como stat BASE (no es un buff), así que pasar de Berserker a Pícaro
        //     dejaba al Pícaro robando vida — el servidor ya no lo tenía, pero el valor
        //     seguía vivo en NetStats y volvía al ASC en la próxima resincronización.
        //   · Un stat que la clase nueva SÍ declara tampoco viajaba: recién construido,
        //     su CurrentValue ya es el correcto, así que RecalculateAllAttributes no ve
        //     ningún cambio y no notifica nada.
        //
        // Los que desaparecieron se avisan en CERO, que es lo que GetAttributeValue
        // devuelve para un atributo ausente: así el valor sincronizado dice lo mismo
        // que el del servidor.
        foreach (EAttributeType gone in _clearedAttributeKeys)
            if (!Attributes.ContainsKey(gone))
                OnAttributeChangedCallback?.Invoke(gone, 0f);

        foreach (var pair in Attributes)
            OnAttributeChangedCallback?.Invoke(pair.Key, pair.Value.CurrentValue);
    }

    // Atributos que tenía el personaje justo antes del último InitializeAttributes.
    // Es un campo y no una variable local para no reservar una lista en cada cambio
    // de clase; solo lo usa ese método.
    private readonly List<EAttributeType> _clearedAttributeKeys = new List<EAttributeType>();

    // Lee el valor actual de un atributo (0 si el personaje no lo tiene).
    public float GetAttributeValue(EAttributeType type)
        => Attributes.ContainsKey(type) ? Attributes[type].CurrentValue : 0f;

    // Escribe directamente el valor actual de un atributo (sin pasar por
    // un GameplayEffect). La usan ExecuteInstantEffect, level-ups, etc.
    // Dispara OnAttributeChangedCallback y, si deja la Vida en 0, Die().
    public void SetCurrentAttributeValue(EAttributeType type, float val)
    {
        if (type == EAttributeType.Health && val < 1f && HasTag(EGameplayTag.Status_Immortal))
            val = 1f;

        // Clampeo de "pools": la vida (y el maná) nunca quedan en negativo ni
        // superan su máximo. Sin esto, un golpe fuerte dejaba la Vida en negativo
        // (ej. -30/100), que se colaba a la barra de vida y a los cálculos que
        // escalan con la vida faltante (MaxHealth - Health). El tope superior solo
        // se aplica si el Max existe y es > 0 (los NPC/objetos sin MaxHealth
        // definido no se ven afectados). El chequeo de muerte de más abajo sigue
        // disparándose porque val queda exactamente en 0.
        if (type == EAttributeType.Health)
        {
            float max = GetAttributeValue(EAttributeType.MaxHealth);
            val = max > 0f ? Mathf.Clamp(val, 0f, max) : Mathf.Max(0f, val);
        }
        else if (type == EAttributeType.Mana)
        {
            float max = GetAttributeValue(EAttributeType.MaxMana);
            val = max > 0f ? Mathf.Clamp(val, 0f, max) : Mathf.Max(0f, val);
        }
        else if (type == EAttributeType.Energy)
        {
            // La energía es un pool igual que Vida/Maná: la regeneración la empuja
            // hacia arriba (un GE pasivo con Period) y el escudo la consume. Sin este
            // clamp, la regeneración se pasaba de MaxEnergy sin techo y la barra del
            // HUD quedaba por encima del 100% (y el escudo duraba de más).
            float max = GetAttributeValue(EAttributeType.MaxEnergy);
            val = max > 0f ? Mathf.Clamp(val, 0f, max) : Mathf.Max(0f, val);
        }
        else if (type == EAttributeType.Shield)
        {
            val = Mathf.Max(0f, val);
        }

        // La medición del balance (ver CombatLog): toda vida que se GANA, con quién la causó. Revivir (desde 0) no
        // cuenta: no es curación.
        if (TraceBalance && type == EAttributeType.Health && GetAttributeValue(EAttributeType.Health) > 0f &&
            val > GetAttributeValue(EAttributeType.Health))
            RecordBalanceHeal(val - GetAttributeValue(EAttributeType.Health));

        if (Attributes.ContainsKey(type)) Attributes[type].CurrentValue = val;
        else Attributes[type] = new AttributeValue(val);

        OnAttributeChangedCallback?.Invoke(type, val);

        if (type == EAttributeType.Health && val <= 0 && !HasTag(EGameplayTag.State_Dead))
            Die();

        // Si lo que acaba de cambiar es un TECHO y quedó por debajo de su pool, hay que
        // bajar el pool: el clamp de arriba solo actúa cuando se escribe el pool, así
        // que un máximo que BAJA dejaba la vida por encima de su tope (ej. 120/100 al
        // pasar del Bárbaro —120 de vida— al Paladín —100—, o al expirar un buff de
        // vida máxima). Se veía "120/100" hasta el primer golpe o curación, que recién
        // ahí escribía la Vida y la clampeaba de golpe.
        ClampPoolToMax(type, EAttributeType.MaxHealth,  EAttributeType.Health);
        ClampPoolToMax(type, EAttributeType.MaxMana,    EAttributeType.Mana);
        ClampPoolToMax(type, EAttributeType.MaxEnergy,  EAttributeType.Energy);
    }

    // Si 'changed' es el atributo techo indicado y el pool asociado lo supera, baja el
    // pool hasta el techo. La reentrada es segura: vuelve a entrar a
    // SetCurrentAttributeValue con el POOL, y ese camino no toca ningún techo.
    private void ClampPoolToMax(EAttributeType changed, EAttributeType maxType, EAttributeType poolType)
    {
        if (changed != maxType) return;

        float max = GetAttributeValue(maxType);
        if (max <= 0f) return; // sin techo definido no clampeamos (NPCs/objetos)

        if (GetAttributeValue(poolType) > max)
            SetCurrentAttributeValue(poolType, max);
    }

    // Sube permanentemente el valor BASE de un atributo (ej: al elegir una
    // mejora roguelike). Si sube MaxHealth/MaxMana, también cura/rellena
    // esa misma cantidad en el valor actual.
    public void UpgradeAttribute(EAttributeType type, float amount)
    {
        if (!Attributes.ContainsKey(type)) return;
        Attributes[type].BaseValue += amount;
        RecalculateAllAttributes();
        if (type == EAttributeType.MaxHealth) SetCurrentAttributeValue(EAttributeType.Health, GetAttributeValue(EAttributeType.Health) + amount);
        if (type == EAttributeType.MaxMana)   SetCurrentAttributeValue(EAttributeType.Mana,   GetAttributeValue(EAttributeType.Mana)   + amount);
    }

    // Recalcula CurrentValue = (Base + Aditivos) * Multiplicativos para
    // todos los atributos "derivados" (no toca Vida/Maná/Energía/Exp/
    // Nivel/Escudo, que se manejan aparte porque son "pools" con su
    // propio valor actual independiente del recálculo).
    // True si el atributo es un "POOL": tiene su propio valor actual que sube y baja
    // por consumo (daño, gasto, regeneración) en vez de calcularse como
    // (Base + Aditivos) × Multiplicativos.
    //
    // Los pools quedan afuera del sistema de modificadores por completo: no se
    // recalculan (RecalculateAllAttributes los saltea, o el recálculo les pisaría el
    // valor actual) y un modificador CON DURACIÓN sobre uno de ellos es inerte —
    // para tocarlos hay que usar Period (por ticks) o Shield. Ver WarnInertPoolModifiers.
    private static bool IsPoolAttribute(EAttributeType type)
    {
        return type == EAttributeType.Health || type == EAttributeType.Mana   ||
               type == EAttributeType.Energy || type == EAttributeType.Exp    ||
               type == EAttributeType.MaxExp || type == EAttributeType.Level  ||
               type == EAttributeType.Shield;
    }

    // El tope de velocidad de ataque: nunca menos de esto entre ataque y ataque, por
    // más bonos que se apilen. 0.3 s = 3.3 ataques por segundo.
    public const float MinAttackInterval = 0.3f;

    private void RecalculateAllAttributes()
    {
        foreach (var pair in Attributes)
        {
            EAttributeType type = pair.Key;
            AttributeValue attr = pair.Value;

            if (IsPoolAttribute(type)) continue;

            float newValue = (attr.BaseValue + attr.AdditiveModifier) * attr.MultiplicativeModifier;

            // Piso de velocidad de ataque: AtkSpeed son "segundos entre ataques"
            // (menor = más rápido), así que un mínimo limita la velocidad MÁXIMA
            // por más buffs que se apilen (ej. rage + tótem del tigre). Evita
            // que el personaje quede atacando absurdamente rápido.
            if (type == EAttributeType.AtkSpeed) newValue = Mathf.Max(newValue, MinAttackInterval);

            if (newValue == attr.CurrentValue) continue;

            attr.CurrentValue = newValue;

            // Notificar el cambio para que NetworkAbilitySystemComponent lo
            // sincronice a los clientes. Antes se escribía CurrentValue directo
            // sin disparar el callback — por eso los buffs que cambian stats
            // vía modificadores (MaxHealth, AtkSpeed, MovSpeed...) se aplicaban
            // en el servidor pero NUNCA llegaban a los clientes remotos: la UI
            // del jugador 2 mostraba MaxHealth viejo (ej. 142/120) y no recibía
            // el buff de velocidad de ataque/movimiento.
            OnAttributeChangedCallback?.Invoke(type, newValue);
        }
    }

    // Combina el valor actual de un atributo con un Modifier según su tipo
    // (Add/Multiply/Override). La usa ExecuteInstantEffect.
    private float CalculateModifiedValue(float current, Modifier mod, float magnitude)
    {
        switch (mod.Type)
        {
            case Modifier.EModificationType.Add:      return current + magnitude;
            case Modifier.EModificationType.Multiply: return current * magnitude;
            case Modifier.EModificationType.Override: return magnitude;
            default: return current;
        }
    }
}
