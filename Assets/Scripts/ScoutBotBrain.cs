using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Nuevo sistema de Scouts basado en BaseGridBot y BotManager.
/// Flujo:
/// 1. Llega a DSPos inicial (espera turno).
/// 2. Recibe una lista de tiles a inspeccionar (AssignRoute).
/// 3. Recorre cada tile, tarda photoTime segundos tomando "foto".
/// 4. Regresa a DSPos y entrega información (unloadTime segundos).
/// 5. Vuelve a home.
/// </summary>
public class ScoutBotBrain : BaseGridBot
{
    [Header("Config Scout")]
    public float photoTime = 0.5f;   // tiempo por "foto" en cada planta
    public float unloadTime = 2f;    // tiempo entregando info en DS final

    [Header("Stations")]
    public Transform dsTransform;    // punto DSPos (misma DS que usan los pickbots)

    [Header("Home")]
    [Tooltip("Punto al que regresará al final de la misión. Si se deja vacío, se usa la posición inicial.")]
    public Transform homeTransform; // home explícito

    [Header("Movement / Visual")]
    public float stepsPerSecond = 2f;
    public float fixedY = 1.71f;

    [Header("Animation")]
    private Animator animator;

    [Header("Mission State")]
    public bool MissionComplete { get; private set; }

    private GridManager grid;
    private BotManager botManager;

    private Vector2Int dsGridPos;
    private Vector2Int homeGridPos;

    // Ruta de inspección (tiles frente a plantas)
    // routeTiles = lista original (solo referencia/debug)
    // pendingTiles = tiles que aún faltan por visitar (para el greedy)
    private List<Vector2Int> routeTiles = new();
    private List<Vector2Int> pendingTiles = new();

    // Flag para saber si ya se asignó una ruta (para distinguir “no me han dado ruta” de “terminé todos los tiles”)
    private bool routeAssigned = false;

    // Path actual dentro del grid
    private List<Vector2Int> currentPath = new();
    private int currentPathIndex = 0;

    // Interpolación entre tiles (igual que en BotController)
    private Vector3 worldFrom;
    private Vector3 worldTo;
    private float stepProgress = 1f; // 1 = ya está en el tile destino

    private bool forceReplan = false;
    private bool busy = false;

    private enum ScoutState
    {
        GoingToDS_Initial,
        WaitingAtDS_Initial,
        InspectingRoute,
        GoingToDS_Final,
        WaitingAtDS_Final,
        ReturningHome,
        Idle
    }

    private ScoutState state = ScoutState.GoingToDS_Initial;

    // ----------------- BaseGridBot Required -----------------

    public override float RemainingCost
    {
        get
        {
            int routeRemaining = (pendingTiles != null ? pendingTiles.Count : 0);
            int pathRemaining = currentPath.Count - currentPathIndex;
            return routeRemaining * 5 + pathRemaining;
        }
    }

    public override void ForceReplan()
    {
        forceReplan = true;
    }

    // ----------------- UNITY -----------------

    private void Start()
    {
        MissionComplete = false;
        grid = GridManager.Instance;
        botManager = BotManager.Instance;

        animator = GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning($"{name}: ScoutBotBrain no encontró Animator en hijos.");
        }

        // Posición inicial en el mundo -> grid
        CurrentGridPos = grid.WorldToGrid(transform.position);
        transform.position = grid.GridToWorld(CurrentGridPos);
        FixY();

        // Definimos home:
        if (homeTransform != null)
        {
            homeGridPos = grid.WorldToGrid(homeTransform.position);
        }
        else
        {
            homeGridPos = CurrentGridPos; // si no hay homeTransform, usamos la posición inicial
        }

        // Posición de la Docking Station (DSPos)
        if (dsTransform != null)
        {
            dsGridPos = grid.WorldToGrid(dsTransform.position);
        }
        else
        {
            Debug.LogWarning($"{name}: dsTransform no asignado, usando posición inicial como DS.");
            dsGridPos = CurrentGridPos;
        }

        // Solo avisamos si home no es caminable
        if (!grid.IsWalkable(homeGridPos))
        {
            Debug.LogWarning($"{name}: homeGridPos {homeGridPos} no es walkable. Revisa la posición de homeTransform o del bot.");
        }

        // Registrar en BotManager (para colisiones, prioridad, etc.)
        botManager.RegisterBot(this, CurrentGridPos);

        // Inicializar interpolación
        worldFrom = transform.position;
        worldTo = transform.position;
        stepProgress = 1f;

        // Estado inicial: yendo a DS
        state = ScoutState.GoingToDS_Initial;
        SetAnimationState(1);
        TryGoToDSInitial();
    }

    private void Update()
    {
        if (busy) return;

        // 1) Si estamos a medio paso entre tiles, seguir interpolando
        if (stepProgress < 1f)
        {
            float delta = Time.deltaTime * stepsPerSecond;
            stepProgress += delta;
            float t = Mathf.Clamp01(stepProgress);
            transform.position = Vector3.Lerp(worldFrom, worldTo, t);
            FixY();
            return;
        }

        // 2) Si no estamos a medio paso, avanzar en el path (si hay)
        MoveAlongPath();

        // 3) Estados que requieren reintentar reclamar DS (si estaba ocupada)
        switch (state)
        {
            case ScoutState.WaitingAtDS_Initial:
                TryGoToDSInitial();
                break;

            case ScoutState.WaitingAtDS_Final:
                TryGoToDSFinal();
                break;
        }
    }

    // ----------------- PUBLIC API -----------------

    /// <summary>
    /// MissionController llama esto para asignar la ruta de inspección.
    /// </summary>
    public void AssignRoute(List<Vector2Int> tiles)
    {
        routeTiles = tiles ?? new List<Vector2Int>();
        pendingTiles = new List<Vector2Int>(routeTiles);
        routeAssigned = true;

        // Si ya está en modo de inspección y estaba esperando ruta, arranca
        if (state == ScoutState.InspectingRoute && !busy && pendingTiles.Count > 0)
        {
            GoToNextInspectionTile();
        }
    }

    // ----------------- MOVIMIENTO ENTRE TILES -----------------

    private void MoveAlongPath()
    {
        // Si no hay ruta, no hacemos nada aquí
        if (currentPath == null || currentPath.Count == 0)
            return;

        if (forceReplan && currentPath.Count > 0)
        {
            Vector2Int finalTarget = currentPath[currentPath.Count - 1];
            currentPath = grid.FindPath(CurrentGridPos, finalTarget, this);
            currentPathIndex = 0;
            forceReplan = false;
        }

        if (currentPathIndex >= currentPath.Count)
        {
            // Llegamos al destino de este path
            currentPath.Clear();
            currentPathIndex = 0;
            OnArrivedToTile();
            return;
        }

        Vector2Int next = currentPath[currentPathIndex];

        bool canMove = botManager.TryMoveWithPriority(this, CurrentGridPos, next);
        if (!canMove) return;

        // Configurar el siguiente paso
        worldFrom = grid.GridToWorld(CurrentGridPos);
        CurrentGridPos = next;
        worldTo = grid.GridToWorld(CurrentGridPos);

        // Giro hacia la dirección del movimiento
        Vector3 dir = worldTo - worldFrom;
        if (dir.sqrMagnitude > 0.01f)
        {
            dir.y = 0;
            Quaternion targetRot = Quaternion.LookRotation(dir);
            transform.rotation = targetRot;
        }

        worldFrom.y = fixedY;
        worldTo.y = fixedY;

        stepProgress = 0f;
        currentPathIndex++;

        // Asegurar que la animación esté en "mover"
        SetAnimationState(1);
    }

    private void FixY()
    {
        var p = transform.position;
        p.y = fixedY;
        transform.position = p;
    }

    private void SetTarget(Vector2Int target)
    {
        // Caso especial: ya estamos parados en el tile destino
        if (target == CurrentGridPos)
        {
            currentPath.Clear();
            currentPathIndex = 0;
            OnArrivedToTile();
            return;
        }

        currentPath = grid.FindPath(CurrentGridPos, target, this);
        currentPathIndex = 0;

        if (currentPath == null || currentPath.Count == 0)
        {
            Debug.LogWarning($"{name}: no hay camino a {target}. Me quedo en estado {state}.");
            return;
        }
    }

    // ----------------- LÓGICA DE ESTADOS -----------------

    private void OnArrivedToTile()
    {
        switch (state)
        {
            case ScoutState.GoingToDS_Initial:
                // SOLO hacemos briefing si realmente estamos en el tile de DS
                if (CurrentGridPos == dsGridPos)
                    StartCoroutine(WaitAtDSInitial());
                break;

            case ScoutState.GoingToDS_Final:
                if (CurrentGridPos == dsGridPos)
                    StartCoroutine(WaitAtDSFinal());
                break;

            case ScoutState.InspectingRoute:
                StartCoroutine(DoInspection());
                break;

            case ScoutState.ReturningHome:
                // Solo marcamos MissionComplete si realmente llegó a homeGridPos
                if (CurrentGridPos == homeGridPos)
                {
                    state = ScoutState.Idle;
                    SetAnimationState(0);
                    MissionComplete = true;
                    Debug.Log($"[Scout] {name} completó su misión y regresó a home {homeGridPos}.");
                }
                else
                {
                    Debug.LogWarning(
                        $"[Scout] {name} terminó path en {CurrentGridPos} pero home es {homeGridPos}. Reintentando ir a casa."
                    );
                    SetTarget(homeGridPos);
                }
                break;
        }
    }

    // --- FASE 1: IR / HACER FILA EN DS INICIAL (patólogo) ---

    private void TryGoToDSInitial()
    {
        // 1) Intentar ser dueño de DS (igual que BotController)
        if (botManager.TryClaimDocking(this))
        {
            // Si estábamos en un slot de fila, lo liberamos
            botManager.ReleaseDockingQueueSlot(this);

            // Ir directamente al tile de DS
            state = ScoutState.GoingToDS_Initial;
            SetTarget(dsGridPos);
            return;
        }

        // 2) No hay turno -> intentar obtener un slot de fila
        if (botManager.TryGetDockingQueueSlot(this, out var queuePos))
        {
            if (CurrentGridPos != queuePos)
            {
                // Ir al slot de fila, pero el estado lógico es "esperando DS"
                SetTarget(queuePos);
            }
        }

        // 3) Estado pasa a "esperando en DS"
        if (state == ScoutState.GoingToDS_Initial)
            state = ScoutState.WaitingAtDS_Initial;
    }

    private IEnumerator WaitAtDSInitial()
    {
        busy = true;
        state = ScoutState.WaitingAtDS_Initial;

        // Idle en DS mientras recibe órdenes
        SetAnimationState(0);
        yield return new WaitForSeconds(1.0f);

        busy = false;
        botManager.ReleaseDocking(this);

        // Empieza la fase de inspección
        state = ScoutState.InspectingRoute;

        // Si ya tenemos ruta asignada, arranca; si no, espera a que llegue
        if (routeAssigned && pendingTiles != null && pendingTiles.Count > 0)
        {
            GoToNextInspectionTile();
        }
        else
        {
            StartCoroutine(WaitForRoute());
        }
    }

    // --- FASE 2: INSPECCIÓN ---

    private Vector2Int PickClosestTile(List<Vector2Int> tiles, Vector2Int from)
    {
        Vector2Int best = from;
        int bestDist = int.MaxValue;

        for (int i = 0; i < tiles.Count; i++)
        {
            Vector2Int t = tiles[i];
            int d = Mathf.Abs(t.x - from.x) + Mathf.Abs(t.y - from.y); // distancia Manhattan

            if (d < bestDist)
            {
                bestDist = d;
                best = t;
            }
        }

        return best;
    }

    private void GoToNextInspectionTile()
    {
        // Si no hay tiles pendientes, ya terminamos el recorrido -> ir a DS final
        if (pendingTiles == null || pendingTiles.Count == 0)
        {
            state = ScoutState.GoingToDS_Final;
            TryGoToDSFinal();
            return;
        }

        // Elegir el tile más cercano desde la posición actual
        Vector2Int nextTile = PickClosestTile(pendingTiles, CurrentGridPos);
        pendingTiles.Remove(nextTile);

        SetTarget(nextTile);
        SetAnimationState(1);
    }

    private IEnumerator WaitForRoute()
    {
        busy = true;
        SetAnimationState(0);

        // Esperamos hasta que el MissionController asigne la ruta
        while (!routeAssigned || pendingTiles == null || pendingTiles.Count == 0)
        {
            yield return null;
        }

        busy = false;
        // Ya tenemos ruta → ir al primer tile
        GoToNextInspectionTile();
    }

    private IEnumerator DoInspection()
    {
        busy = true;

        // Paramos animación de caminar mientras "toma foto"
        SetAnimationState(0);

        // Simula tomar foto photoTime segundos
        yield return new WaitForSeconds(photoTime);

        busy = false;

        // Aquí luego generaremos Observaciones ligadas a la celda actual (CurrentGridPos)

        // Al terminar esta inspección, ir al siguiente tile pendiente (el más cercano)
        GoToNextInspectionTile();
    }

    // --- FASE 3: DS FINAL (de regreso al patólogo) ---

    private void TryGoToDSFinal()
    {
        // 1) Intentar ser dueño de DS
        if (botManager.TryClaimDocking(this))
        {
            botManager.ReleaseDockingQueueSlot(this);

            state = ScoutState.GoingToDS_Final;
            SetTarget(dsGridPos);
            return;
        }

        // 2) No hay turno -> intentar fila física de DS
        if (botManager.TryGetDockingQueueSlot(this, out var queuePos))
        {
            if (CurrentGridPos != queuePos)
            {
                SetTarget(queuePos);
            }
        }

        // 3) Estado pasa a "esperando DS final"
        if (state == ScoutState.GoingToDS_Final)
            state = ScoutState.WaitingAtDS_Final;
    }

    private IEnumerator WaitAtDSFinal()
    {
        busy = true;
        state = ScoutState.WaitingAtDS_Final;

        // Idle mientras descarga info
        SetAnimationState(0);
        yield return new WaitForSeconds(unloadTime);

        busy = false;
        botManager.ReleaseDocking(this);

        // Vuelve a casa
        state = ScoutState.ReturningHome;
        SetTarget(homeGridPos);
        SetAnimationState(1);
    }

    // ----------------- ANIMACIÓN -----------------

    private void SetAnimationState(int stateValue)
    {
        if (animator == null) return;
        animator.SetInteger("State", stateValue);
    }
}
