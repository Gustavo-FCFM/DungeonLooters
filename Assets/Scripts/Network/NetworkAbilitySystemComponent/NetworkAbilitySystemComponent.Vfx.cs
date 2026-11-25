using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;

// ============================================================
// NetworkAbilitySystemComponent — VFX DE HABILIDAD (replicar impactos a todos)
//
// Una parte de NetworkAbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en Network/NetworkAbilitySystemComponent.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class NetworkAbilitySystemComponent
{

    // =========================================================
    // VFX DE HABILIDAD — replicar impactos/efectos a todos los clientes
    //
    // GameplayAbility.PlayImpactVFX() solo hace un Instantiate() local —
    // si algo lo llama desde el servidor (donde corre toda la lógica de
    // daño), ese VFX solo existe en el proceso servidor y ningún cliente
    // remoto lo ve (mismo problema que tuvimos con el arma del proyectil).
    //
    // No podemos mandar la referencia al GameObject del VFX por RPC
    // (FishNet no serializa assets arbitrarios), así que en vez de eso
    // identificamos la habilidad por su SLOT (igual que FindAbilityBySlot
    // para cooldowns) y cada peer reproduce el VFX con SU PROPIA copia
    // local de esa misma habilidad.
    // =========================================================

    // Reproduce el VFX de impacto puntual de una habilidad en el servidor
    // y le avisa a los demás peers que hagan lo mismo con su propia copia.
    [Server]
    public void ServerPlayAbilityVFX(GameplayAbility ability, Vector3 position)
    {
        if (ability == null) return;

        // El servidor reproduce el suyo ya mismo (importa para el host).
        ability.PlayImpactVFX(position);
        AudioManager.Play(ability.ImpactSound, position);

        // Identificamos la habilidad por su índice en GameplayAbilityRegistry
        // (no por slot). Así los observadores la resuelven aunque no tengan la
        // clase equipada, y funciona para pasos de combo que no viven en ningún
        // slot — que era justo lo que fallaba con el esquema por slot.
        int abilityIndex = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;

        if (abilityIndex >= 0)
            ObserversPlayAbilityVFX(abilityIndex, position);
        else
            Debug.LogWarning($"[NetworkASC] '{ability.AbilityName}' no está en GameplayAbilityRegistry — su VFX de impacto no se replicará a los clientes remotos.");
    }

    // RAYOS de una habilidad (la Descarga eléctrica del Mago y sus rebotes): tramos de un punto
    // a otro que cada pantalla dibuja con su copia de la habilidad (GameplayAbility.PlayBeams).
    [Server]
    public void ServerPlayBeams(GameplayAbility ability, Vector3[] from, Vector3[] to)
    {
        if (ability == null || from == null || to == null) return;

        ability.PlayBeams(from, to);   // el servidor ya mismo (importa para el host)

        int abilityIndex = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;
        if (abilityIndex >= 0)
            ObserversPlayBeams(abilityIndex, from, to);
        else
            Debug.LogWarning($"[NetworkASC] '{ability.AbilityName}' no está en GameplayAbilityRegistry — sus rayos no se verán en los clientes remotos.");
    }

    [ObserversRpc]
    private void ObserversPlayBeams(int abilityIndex, Vector3[] from, Vector3[] to)
    {
        if (IsServerInitialized) return;   // el servidor ya los dibujó
        GameplayAbility ability = GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex);
        if (ability != null) ability.PlayBeams(from, to);
    }

    [ObserversRpc]
    private void ObserversPlayAbilityVFX(int abilityIndex, Vector3 position)
    {
        // El servidor ya lo reprodujo en ServerPlayAbilityVFX() de arriba.
        if (IsServerInitialized || _asc == null) return;

        GameplayAbility ability = GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex);
        // PlayImpactVFXFor le presta este ASC como dueño puntual (el template
        // del registro no tiene uno propio; algunos VFX lo necesitan).
        if (ability == null) return;

        ability.PlayImpactVFXFor(_asc, position);
        AudioManager.Play(ability.ImpactSound, position);
    }

    // Como ServerPlayAbilityVFX, pero sobre un PERSONAJE: los VFX "al golpear" de la
    // habilidad. Se manda su NetworkObject en vez de una posición, así cada peer puede
    // pegarle el VFX y que lo siga (la curación de GA_Target sobre un aliado que se mueve).
    // withSound = false cuando ese golpe ya suena por su impacto (el proyectil).
    [Server]
    // when: "al golpear" o "al curar" (ver GameplayAbility.BroadcastHealVFX).
    public void ServerPlayAbilityVFXOn(GameplayAbility ability, AbilitySystemComponent target, bool withSound = true,
                                       EVisualWhen when = EVisualWhen.OnHit)
    {
        if (ability == null || target == null) return;

        ability.PlayImpactVFXOnFor(_asc, target, when);
        if (withSound) AudioManager.Play(ability.ImpactSound, target.transform.position);

        int abilityIndex = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;
        NetworkObject targetNob = target.GetComponent<NetworkObject>();

        if (abilityIndex < 0)
            Debug.LogWarning($"[NetworkASC] '{ability.AbilityName}' no está en GameplayAbilityRegistry — su VFX de impacto no se replicará a los clientes remotos.");
        else if (targetNob != null)
            ObserversPlayAbilityVFXOn(abilityIndex, targetNob, withSound, (byte)when);
        else
            // Sin NetworkObject no se lo puede nombrar por red: en las demás pantallas sale
            // donde estaba (sin seguirlo), que es mejor que no verlo.
            ObserversPlayAbilityHitVFXAt(abilityIndex, target.transform.position, withSound, (byte)when);
    }

    [ObserversRpc]
    private void ObserversPlayAbilityVFXOn(int abilityIndex, NetworkObject target, bool withSound, byte when)
    {
        // El servidor ya lo reprodujo en ServerPlayAbilityVFXOn() de arriba.
        if (IsServerInitialized || target == null) return;

        GameplayAbility ability = GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex);
        AbilitySystemComponent targetAsc = target.GetComponent<AbilitySystemComponent>();
        if (ability == null || targetAsc == null) return;

        ability.PlayImpactVFXOnFor(_asc, targetAsc, (EVisualWhen)when);
        if (withSound) AudioManager.Play(ability.ImpactSound, targetAsc.transform.position);
    }

    [ObserversRpc]
    private void ObserversPlayAbilityHitVFXAt(int abilityIndex, Vector3 position, bool withSound, byte when)
    {
        if (IsServerInitialized || _asc == null) return;

        GameplayAbility ability = GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex);
        if (ability == null) return;

        ability.PlayHitVFXAtFor(_asc, position, (EVisualWhen)when);
        if (withSound) AudioManager.Play(ability.ImpactSound, position);
    }

    // Muestra/oculta el arma en mano de este personaje en TODOS los peers.
    //
    // La usan las habilidades de lanzamiento: el hueso de prop del rig anima el arma
    // como si la soltara, así que mientras vuela el proyectil hay que esconder la real
    // o se ven dos armas. Es puramente cosmético, pero tiene que verse igual en todas
    // las pantallas — por eso va por RPC y no localmente en el servidor.
    [Server]
    // includeOwner: también al dueño REMOTO, que normalmente lo anticipa con su reloj y por eso se lo saltea. Lo usa el
    // bumerán (20 de noviembre de 2026): el dueño no puede saber cuándo vuelve.
    public void ServerSetWeaponVisible(bool visible, bool includeOwner = false)
    {
        SetWeaponVisibleLocal(visible);   // el host ve el cambio ya mismo
        ObserversSetWeaponVisible(visible);
        if (includeOwner && HasOwnerConnection() && !Owner.IsLocalClient) TargetSetWeaponVisible(Owner, visible);
    }

    [TargetRpc]
    private void TargetSetWeaponVisible(NetworkConnection conn, bool visible)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.CancelWeaponHide();   // la anticipación del dueño esperaba para siempre
        SetWeaponVisibleLocal(visible);
    }

    // Al dueño se lo salteamos: él lo anticipa con su propio reloj, en el frame en que
    // SU animación suelta el arma (ver PlayerController.PredictWeaponHide). Este aviso
    // le llegaría dos viajes tarde y se pelearía con esa predicción.
    [ObserversRpc(ExcludeOwner = true)]
    private void ObserversSetWeaponVisible(bool visible)
    {
        // El servidor ya lo aplicó en ServerSetWeaponVisible() de arriba.
        if (IsServerInitialized) return;
        SetWeaponVisibleLocal(visible);
    }

    private void SetWeaponVisibleLocal(bool visible)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.SetMainWeaponVisible(visible);
    }

    // Igual que ServerPlayAbilityVFX, pero para la secuencia automática de
    // GameplayAbility.VisualsSequence (CommitAbility) en vez de un único
    // impacto puntual. Como PlayVisualsSequence() ya calcula todo (delays,
    // offsets, destrucción) a partir de OwnerASC.transform/tags — que están
    // igual en todos los peers — alcanza con arrancar la MISMA corutina en
    // cada uno; no hace falta sincronizar nada más.
    [Server]
    public void ServerPlayAbilityVisualsSequence(GameplayAbility ability)
    {
        if (ability == null || _asc == null) return;

        _asc.StartAbilityCoroutine(ability.PlayVisualsSequence());

        int abilityIndex = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;

        if (abilityIndex >= 0)
            ObserversPlayAbilityVisualsSequence(abilityIndex);
        else
            Debug.LogWarning($"[NetworkASC] '{ability.AbilityName}' no está en GameplayAbilityRegistry — su secuencia de visuales no se replicará a los clientes remotos.");
    }

    [ObserversRpc]
    private void ObserversPlayAbilityVisualsSequence(int abilityIndex)
    {
        // El servidor ya la arrancó en ServerPlayAbilityVisualsSequence() de arriba.
        if (IsServerInitialized || _asc == null) return;

        GameplayAbility ability = GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex);
        // Le pasamos este ASC como dueño puntual: la corutina usa su transform
        // y tags (el template del registro no tiene OwnerASC propio).
        if (ability != null) _asc.StartAbilityCoroutine(ability.PlayVisualsSequence(_asc));
    }
}
