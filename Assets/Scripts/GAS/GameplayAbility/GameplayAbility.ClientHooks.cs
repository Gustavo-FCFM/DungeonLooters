using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — LO QUE PREGUNTA EL CLIENTE
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
//
// Lo que una habilidad puede sobreescribir para el dueño y los demás: la animación de cada
// paso, la predicción, la cámara al mantener, el ícono y la vista previa del lanzamiento.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // LO QUE PREGUNTA EL CLIENTE: animación de los pasos, predicción, cámara, ícono y vista previa
    // =========================================================

    // Clip de un PASO de esta habilidad, si es un combo (ver GA_ComboSequence /
    // GA_AlternatingCombo). Devuelve null en cualquier otra habilidad.
    //
    // Existe para la RED: un paso puede traer un AnimationClipOverride que NO es el
    // clip del asset de la habilidad del paso, así que un observador no lo puede
    // deducir resolviendo esa habilidad en el registro. En vez de mandar el clip (no
    // se puede serializar), se mandan las COORDENADAS del paso y cada peer lo resuelve
    // contra su propia copia del asset del combo — que sí está en el registro por ser
    // la habilidad de nivel superior.
    public virtual AnimationClip GetStepAnimationClip(int sequenceIndex, int stepIndex) => null;

    // De qué habilidad hay que sacar la animación al activar ESTA. Por defecto, de
    // sí misma — que es el caso de todas menos las que DELEGAN en otra.
    //
    // Existe por las habilidades "envoltorio" (GA_TagSwitch: el ataque principal que
    // cambia según un tag). Ahí la animación correcta no es la del envoltorio —que ni
    // siquiera tiene clip— sino la de la variante que realmente se va a ejecutar. Sin
    // este gancho, el dueño predecía la animación del envoltorio (un trigger sin
    // estado asociado) y a los observadores les llegaba su índice de registro, así
    // que ninguno veía el ataque de verdad.
    //
    // Lo consultan PlayerController.ApplyAbilityAnimation (predicción del dueño) y
    // NetworkASC.ServerActivateAbility (réplica a observadores), o sea las DOS puntas.
    // Se puede resolver en el dueño porque los tags se sincronizan.
    public virtual GameplayAbility ResolveAnimationSource() => this;

    // Lo que el DUEÑO puede anticipar de los visuales, además de la animación: se llama
    // en el cliente dueño al apretar, junto con la predicción (ver
    // PlayerController.HandleAbilityInput). Por defecto no hace nada.
    //
    // Existe por el hacha: el servidor la esconde de la mano recién cuando su propia
    // cuenta llega al frame de soltar, y ese aviso vuelve al dueño DOS viajes después
    // de que él ya vio su animación lanzarla. En el host no se nota (viaje cero), pero
    // un cliente conectado veía el hacha todavía en la mano mientras el proyectil ya
    // salía: dos hachas al mismo tiempo.
    public virtual void PredictOwnerVisuals(PlayerController pc) { }

    // Lo mismo, pero al SOLTAR el botón de un mantenido: un lanzamiento apuntado
    // (GA_ProjectileShoot con AimBeforeThrow) muestra el lanzamiento en el acto y esconde
    // el arma a tiempo, sin esperar el viaje al servidor. Lo llama PlayerController.
    public virtual void PredictOwnerReleaseVisuals(PlayerController pc) { }

    // ¿Vale la pena que el dueño ANTICIPE la animación, antes de que el servidor
    // conteste? Por defecto sí. Las de objetivo único dicen que no cuando no hay nadie
    // a tiro: el servidor va a descartar la activación igual, y anticiparla hace que el
    // personaje haga el gesto en el vacío. Se resuelve en el dueño sin problema — la
    // búsqueda es física y tags, las dos cosas están de su lado.
    public virtual bool CanPredictActivation() => true;

    // True si esta habilidad se encarga ELLA de replicar su animación a los
    // observadores, y por lo tanto el servidor no tiene que mandar la suya encima.
    // La usa un combo: cada paso manda la propia (ver GA_ComboSequence). Sin esto, la
    // animación del combo padre llegaba justo después de la del primer paso y la
    // pisaba — en la pantalla de los demás el primer golpe no se veía, solo el segundo.
    public virtual bool BroadcastsOwnAnimation => false;

    // El ícono que el HUD tiene que mostrar AHORA. Casi siempre es AbilityIcon; un
    // GA_TagSwitch con ShowVariantIcon lo cambia según el tag (la postura del Guerrero).
    // UI_AbilitySlot lo revisa cada frame.
    public virtual Sprite CurrentIcon => AbilityIcon;

    // Si mientras se MANTIENE el botón la cámara del dueño se acerca a la mira (los
    // lanzamientos: mantener para apuntar, soltar para lanzar). Lo lee PlayerController
    // del mantenido en curso. Ver GA_ChargedAttack.AimCamera.
    public virtual bool AimsCameraWhileHeld => false;

    // Zoom de la cámara del dueño mientras MANTIENE esta habilidad: su campo de visión × esto
    // (1 = sin zoom). Lo lee PlayerController con los segundos que lleva manteniendo; el arco
    // del Explorador (GA_ChargedAttack) se acerca en cada etapa de la carga.
    public virtual float HeldCameraZoom(float heldSeconds) => 1f;

    // La curva que ve el dueño mientras MANTIENE esta habilidad (TrajectoryPreview): el proyectil
    // que saldría si soltara ahora, con 'heldSeconds' de mantener. null = sin curva. Lo responden
    // el lanzamiento apuntado (GA_ProjectileShoot), el arco por fases (GA_ChargedAttack) y el
    // switch de mantener (GA_HoldTagSwitch, por su variante).
    public virtual GA_ProjectileShoot ProjectileToPreview(float heldSeconds) => null;

    // Las etapas de una carga, para la barra de carga del dueño (UI_ChargeBar): en
    // stageTimes, los segundos en que empieza cada etapa; en maxTime, cuándo se suelta
    // sola. false = esta habilidad no tiene etapas (no se dibuja barra).
    public virtual bool GetChargeStages(List<float> stageTimes, out float maxTime)
    {
        maxTime = 0f;
        return false;
    }
}
