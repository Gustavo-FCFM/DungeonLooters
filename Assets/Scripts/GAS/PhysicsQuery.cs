using System;
using System.Collections.Generic;
using UnityEngine;

// ============================================================
// PhysicsQuery  (búsquedas de física sin crear una lista por llamada — 25 de noviembre de 2026)
//
// Physics.OverlapSphere, OverlapBox, OverlapCapsule, RaycastAll y SphereCastAll crean un arreglo NUEVO en cada
// llamada, y eso es basura para el recolector en cada golpe de cada habilidad. Estas versiones hacen lo mismo con las
// variantes "NonAlloc" de Unity sobre un arreglo PRESTADO de una reserva, y lo devuelven al terminar.
//
// Por qué una reserva y no un solo arreglo compartido: mientras se recorre una búsqueda pasan cosas (un golpe dispara
// una reacción que explota y busca a su vez). Con un arreglo compartido, la segunda búsqueda pisaría a la primera a
// mitad del recorrido. Cada búsqueda toma el suyo.
//
// Se usa así (el resultado se recorre igual que el arreglo de antes: foreach, [i], Length y Sort):
//     using var cols = PhysicsQuery.OverlapSphere(centro, radio, capa);
//     foreach (Collider c in cols) { ... }
// O, si la búsqueda es solo para un foreach, sin variable:
//     foreach (Collider c in PhysicsQuery.OverlapSphere(centro, radio, capa).Once()) { ... }
// (Once: lo devuelve el propio foreach al terminar.)
//
// No hay tope: si la búsqueda llena el arreglo, se repite con uno del doble (lo mismo que encontraba la de Unity).
// ============================================================
public static class PhysicsQuery
{
    private const int StartSize = 32;

    private static readonly Stack<Collider[]>   s_colliders = new Stack<Collider[]>();
    private static readonly Stack<RaycastHit[]> s_rayHits   = new Stack<RaycastHit[]>();

    // ---- Búsquedas de colliders ----

    public static Hits<Collider> OverlapSphere(Vector3 position, float radius, int layerMask = Physics.AllLayers,
                                               QueryTriggerInteraction triggers = QueryTriggerInteraction.UseGlobal)
    {
        Collider[] a = Rent(s_colliders);
        int n;
        while ((n = Physics.OverlapSphereNonAlloc(position, radius, a, layerMask, triggers)) == a.Length) a = Grow(s_colliders, a);
        return new Hits<Collider>(a, n, s_colliders, false);
    }

    public static Hits<Collider> OverlapBox(Vector3 center, Vector3 halfExtents, Quaternion orientation,
                                            int layerMask = Physics.AllLayers,
                                            QueryTriggerInteraction triggers = QueryTriggerInteraction.UseGlobal)
    {
        Collider[] a = Rent(s_colliders);
        int n;
        while ((n = Physics.OverlapBoxNonAlloc(center, halfExtents, a, orientation, layerMask, triggers)) == a.Length) a = Grow(s_colliders, a);
        return new Hits<Collider>(a, n, s_colliders, false);
    }

    public static Hits<Collider> OverlapCapsule(Vector3 point0, Vector3 point1, float radius, int layerMask = Physics.AllLayers,
                                                QueryTriggerInteraction triggers = QueryTriggerInteraction.UseGlobal)
    {
        Collider[] a = Rent(s_colliders);
        int n;
        while ((n = Physics.OverlapCapsuleNonAlloc(point0, point1, radius, a, layerMask, triggers)) == a.Length) a = Grow(s_colliders, a);
        return new Hits<Collider>(a, n, s_colliders, false);
    }

    // ---- Rayos ----

    public static Hits<RaycastHit> RaycastAll(Ray ray, float maxDistance = Mathf.Infinity,
                                              int layerMask = Physics.DefaultRaycastLayers,
                                              QueryTriggerInteraction triggers = QueryTriggerInteraction.UseGlobal)
    {
        RaycastHit[] a = Rent(s_rayHits);
        int n;
        while ((n = Physics.RaycastNonAlloc(ray, a, maxDistance, layerMask, triggers)) == a.Length) a = Grow(s_rayHits, a);
        return new Hits<RaycastHit>(a, n, s_rayHits, false);
    }

    public static Hits<RaycastHit> SphereCastAll(Vector3 origin, float radius, Vector3 direction,
                                                 float maxDistance = Mathf.Infinity,
                                                 int layerMask = Physics.DefaultRaycastLayers,
                                                 QueryTriggerInteraction triggers = QueryTriggerInteraction.UseGlobal)
    {
        RaycastHit[] a = Rent(s_rayHits);
        int n;
        while ((n = Physics.SphereCastNonAlloc(origin, radius, direction, a, maxDistance, layerMask, triggers)) == a.Length)
            a = Grow(s_rayHits, a);
        return new Hits<RaycastHit>(a, n, s_rayHits, false);
    }

    // ---- La reserva ----

    private static T[] Rent<T>(Stack<T[]> pool) => pool.Count > 0 ? pool.Pop() : new T[StartSize];

    // Lleno: puede haber más. Se devuelve el chico y se busca de nuevo con uno del doble.
    private static T[] Grow<T>(Stack<T[]> pool, T[] full)
    {
        T[] bigger = new T[full.Length * 2];
        Return(pool, full, full.Length);
        return bigger;
    }

    internal static void Return<T>(Stack<T[]> pool, T[] array, int used)
    {
        if (array == null) return;
        Array.Clear(array, 0, Mathf.Min(used, array.Length));   // que no retenga objetos destruidos
        // Devolverlo dos veces lo prestaría a dos búsquedas a la vez: se revisa.
        foreach (T[] a in pool) if (ReferenceEquals(a, array)) return;
        pool.Push(array);
    }

    // ============================================================
    // El resultado: se recorre como el arreglo de antes y se devuelve con Dispose (using) o, con Once, al terminar el foreach.
    // ============================================================
    public readonly struct Hits<T> : IDisposable
    {
        private readonly T[] _array;
        private readonly Stack<T[]> _pool;
        private readonly bool _returnAfterForeach;

        public int Length { get; }

        internal Hits(T[] array, int count, Stack<T[]> pool, bool returnAfterForeach)
        {
            _array = array;
            Length = count;
            _pool = pool;
            _returnAfterForeach = returnAfterForeach;
        }

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
                return _array[index];
            }
        }

        // Para recorrerlo en un solo foreach sin variable: lo devuelve el foreach al terminar.
        public Hits<T> Once() => new Hits<T>(_array, Length, _pool, true);

        // Ordena lo encontrado (lo mismo que System.Array.Sort sobre el arreglo de antes).
        public void Sort(Comparison<T> comparison)
        {
            if (Length > 1) Array.Sort(_array, 0, Length, Comparer<T>.Create(comparison));
        }

        public void Dispose() => Return(_pool, _array, Length);

        public Enumerator GetEnumerator() => new Enumerator(this);

        public struct Enumerator : IDisposable
        {
            private readonly Hits<T> _hits;
            private int _index;

            internal Enumerator(Hits<T> hits) { _hits = hits; _index = -1; }

            public T Current => _hits._array[_index];
            public bool MoveNext() => ++_index < _hits.Length;

            public void Dispose()
            {
                if (_hits._returnAfterForeach) _hits.Dispose();
            }
        }
    }
}
