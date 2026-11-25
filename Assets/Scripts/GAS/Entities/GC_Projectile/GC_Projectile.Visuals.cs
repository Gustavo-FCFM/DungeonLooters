using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;

// ============================================================
// GC_Projectile — VISUALES (el modelo del arma)
//
// Una parte de GC_Projectile (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/GC_Projectile.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GC_Projectile
{

    // =========================================================
    // VISUALES — el modelo del arma y el placeholder por defecto
    // =========================================================

    // Cuando llega (o cambia) el dueño sincronizado, intenta aplicar la
    // visual de su arma.
    private void OnShooterChanged(NetworkObject prev, NetworkObject next, bool asServer)
    {
        if (next != null) TryApplyShooterWeaponVisuals(next);
    }

    // Resuelve el PlayerController del dueño EN ESTE PROCESO y le pide su
    // arma actual (currentMainWeapon) — cada peer instancia esa arma
    // localmente como parte de EquipCharacterClass, así que no hace falta
    // sincronizar la malla en sí, solo QUIÉN es el dueño.
    private void TryApplyShooterWeaponVisuals(NetworkObject shooterNob)
    {
        if (_weaponVisualsApplied) return; // idempotente: OnChange + el chequeo en OnStartClient pueden pisarse

        PlayerController pc = shooterNob != null ? shooterNob.GetComponent<PlayerController>() : null;
        GameObject weapon   = pc != null ? pc.GetCurrentMainWeapon() : null;

        if (weapon == null)
        {
            // Quien disparó no tiene arma que clonar: un NPC (los magos fantasma, por
            // ejemplo) o un jugador sin arma equipada. En ese caso el proyectil se
            // muestra tal como viene el prefab — que para eso trae su propio modelo.
            SetDefaultVisuals(true);
            _weaponVisualsApplied = true;   // ya está resuelto: no hay que seguir intentando
            return;
        }

        OverrideVisuals(weapon);
        _weaponVisualsApplied = true;
    }

    // Red de seguridad del temporizador de Awake: si nadie resolvió los visuales, el
    // proyectil se muestra con su propio modelo en vez de quedar invisible.
    private void ShowDefaultVisualsIfNoSwap()
    {
        if (_weaponVisualsApplied) return;

        SetDefaultVisuals(true);
        _weaponVisualsApplied = true;
    }

    // Prende o apaga los Renderer PROPIOS del prefab (los que había al crearse), sin
    // importar si están en la raíz o en un hijo. No toca el arma clonada.
    private void SetDefaultVisuals(bool visible)
    {
        if (_defaultRenderers == null) return;

        foreach (Renderer r in _defaultRenderers)
            if (r != null) r.enabled = visible;
    }

    // Clona el modelo del arma real como hijo visual del proyectil,
    // ajusta su posición/rotación, y le quita los colliders (para que no
    // interfieran con el collider principal del proyectil).
    public void OverrideVisuals(GameObject weaponModel)
    {
        if (weaponModel == null) return;

        // El modelo propio del prefab ya se apagó en Awake — acá solo clonamos el arma
        // real. Si nunca llegamos hasta acá (un NPC, que no tiene arma), ese modelo se
        // vuelve a prender: ver TryApplyShooterWeaponVisuals.
        SetDefaultVisuals(false);

        GameObject weaponClone = Instantiate(weaponModel, transform);

        // El arma del jugador puede estar OCULTA justo ahora: las habilidades de
        // lanzamiento la apagan al soltar (HideWeaponWhileFlying), y este clon se arma
        // en CADA peer cuando le llega el SyncVar del tirador — que puede ser uno o
        // varios frames despues. Clonar un arma ya apagada dejaba el proyectil
        // invisible: no se veia ni el arma en mano ni el hacha volando.
        //
        // El clon siempre nace visible: su estado no tiene por que heredar el del
        // original, que a esta altura ya no representa nada.
        foreach (Renderer r in weaponClone.GetComponentsInChildren<Renderer>(true))
            if (r != null) r.enabled = true;

        weaponClone.transform.localPosition = Vector3.zero;
        weaponClone.transform.localRotation = Quaternion.Euler(0f,90f,0f); // O la rotación que necesites para que apunte bien

        var colliders = weaponClone.GetComponentsInChildren<Collider>();
        foreach (var col in colliders) Destroy(col);

        // La estela del arma NO se clona. Un TrailRenderer guarda sus puntos en espacio
        // de MUNDO, así que el clon nace con los puntos que la estela tenía en la mano
        // del jugador: mientras el proyectil vuela, esa estela dibuja una línea desde la
        // mano hasta el arma, y al impactar esa línea es lo último que queda en pantalla
        // — se ve como si el arma "volviera" desde el punto de impacto hasta la mano.
        //
        // Además la estela del arma la prenden y apagan los AnimationEvents del clip
        // (EnableTrail/DisableTrail), que solo existen en el dueño: el clon nunca
        // recibiría el apagado y seguiría emitiendo todo el vuelo.
        //
        // Si un proyectil necesita estela propia, va en SU prefab, no en la del arma.
        foreach (var trail in weaponClone.GetComponentsInChildren<TrailRenderer>(true))
            Destroy(trail);

        ApplySpinBlur(weaponClone);
        FitColliderToModel(weaponClone);
    }

    // La caja del proyectil, del tamaño del arma que se ve volar (pedido de Gustavo, 19 de
    // octubre de 2026): con la caja de 1 m de antes, tirar cerca de una pared pegaba en la
    // pared. Se mide con las mallas (no con los Renderer, que pueden estar apagados) en el
    // espacio del proyectil, así gira con él (el hacha que da vueltas). Corre en todos los
    // peers; el que choca es el servidor (ver Sweep). Acertarle a la gente no se achica: eso
    // lo decide aparte el "Radio contra personajes".
    private void FitColliderToModel(GameObject model)
    {
        if (!FitColliderToWeapon || !(GetComponent<Collider>() is BoxCollider box)) return;

        Matrix4x4 toLocal = transform.worldToLocalMatrix;
        Bounds bounds = default;
        bool any = false;

        foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
        {
            Mesh mesh = null;
            if (r is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
            else if (r is MeshRenderer)
            {
                MeshFilter filter = r.GetComponent<MeshFilter>();
                if (filter != null) mesh = filter.sharedMesh;
            }
            if (mesh == null) continue;   // partículas, estelas: no son el arma

            Matrix4x4 m = toLocal * r.transform.localToWorldMatrix;
            Vector3 c = mesh.bounds.center, e = mesh.bounds.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x,
                                                 (i & 2) == 0 ? -e.y : e.y,
                                                 (i & 4) == 0 ? -e.z : e.z);
                Vector3 p = m.MultiplyPoint3x4(corner);
                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                else bounds.Encapsulate(p);
            }
        }
        if (!any) return;

        float min = Mathf.Max(0.01f, MinColliderSize);
        box.center = bounds.center;
        box.size   = new Vector3(Mathf.Max(min, bounds.size.x), Mathf.Max(min, bounds.size.y),
                                 Mathf.Max(min, bounds.size.z));
    }

    // Le monta el blur de giro (paquete Simple Spin Blur) a cada malla del arma
    // clonada. El efecto dibuja copias fantasma del mesh entre los frames de rotacion,
    // asi que aparece SOLO cuando el proyectil gira de verdad: un hacha lanzada con
    // AddSpin lo muestra, un proyectil recto que no rota no dibuja nada gracias al
    // corte por velocidad angular.
    //
    // Sin material asignado no se hace nada y el proyectil vuela como siempre.
    private void ApplySpinBlur(GameObject weaponClone)
    {
        if (SpinBlurMaterial == null) return;

        // Solo las mallas mas GRANDES reciben el blur — en un arma, las hojas. El mango
        // y los adornos son piezas chicas cuyo blur no se distingue y que multiplican el
        // costo: el hacha sola tiene siete mallas.
        //
        // Se ordena por VOLUMEN de bounds y NO por nombre a proposito. Un filtro por
        // "blade" moriria en la primera espada que se llame distinto, y en este proyecto
        // los assets se renombran seguido.
        List<MeshFilter> meshes = new List<MeshFilter>();
        foreach (MeshFilter mf in weaponClone.GetComponentsInChildren<MeshFilter>(true))
        {
            // El Start() del paquete hace GetComponent<MeshFilter>().mesh sin chequear:
            // una malla vacia lo tira abajo.
            if (mf.sharedMesh != null) meshes.Add(mf);
        }

        meshes.Sort((a, b) => BoundsVolume(b.sharedMesh).CompareTo(BoundsVolume(a.sharedMesh)));

        int count = Mathf.Min(SpinBlurMaxMeshes, meshes.Count);
        for (int i = 0; i < count; i++)
        {
            SimpleSpinBlur blur = meshes[i].gameObject.AddComponent<SimpleSpinBlur>();
            blur.SSB_Material = SpinBlurMaterial;
            blur.shutterSpeed = SpinBlurShutterSpeed;
            blur.Samples      = SpinBlurSamples;
            blur.alphaOffset  = SpinBlurAlpha;

            // advancedSettings se arma a mano a proposito: AddComponent no pasa por el
            // deserializador de Unity, asi que el campo llega en NULL — y el Start() del
            // paquete lo desreferencia sin chequear. Sin esto, excepcion en cada tiro.
            blur.advancedSettings = new AdvancedSettings
            {
                AngularVelocityCutoff = SpinBlurAngularCutoff,
                enableGPUInstancing   = true,
                subMaterialIndex      = 0,
                unitLocalScale        = false
            };
        }
    }

    // Volumen de la caja envolvente de una malla. Sirve para separar las piezas con
    // presencia visual (hojas, cabezas de maza) de los adornos, sin depender de como
    // se llamen los objetos.
    private static float BoundsVolume(Mesh mesh)
    {
        Vector3 size = mesh.bounds.size;
        return size.x * size.y * size.z;
    }
}
