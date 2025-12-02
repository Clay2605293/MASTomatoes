using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BotController : BaseGridBot
{
    // ---------------- ESTADOS ----------------

    public enum BotState
    {
        GoingToDS_Initial,
        WaitingAtDS_Initial,
        Harvesting,
        GoingToEC,
        WaitingForEC,
        Unloading,
        GoingToDS_Final,
        WaitingAtDS_Final,
        ReturningHome,
        IdleFinished
    }

    [Header("Estado (solo lectura)")]
    public BotState state = BotState.GoingToDS_Initial;

    // ---------------- CONFIG GENERAL ----------------

    [Header("Velocidad en tiles")]
    public float stepsPerSecond = 2f; // cuántos tiles por segundo recorre

    [Header("Stations")]
    public Transform dockingStationTransform; // DS
    public Transform ecTransform;             // EC

    [Header("Tomates")]
    public int capacity = 5;
    public float perTomatoPickupTime = 0.1f;
    public float perTomatoDropTime = 0.1f;
    public float dockWaitSeconds = 2f;

    [Header("Animation")]
    private Animator animator;
    public float fixedYPosition = 1.71f;     // Fixed Y position for bot

    [Header("Mission State")]
    public bool MissionComplete { get; private set; }
    private bool missionStarted = false;

    // ---------------- CAMPOS PRIVADOS ----------------

    private GridManager grid;
    private BotManager botManager;

    // --- Estadísticas de recolección ---
    [Header("Stats de recolección")]
    [Tooltip("Cuántos tomates debe recolectar este bot para medir su tiempo")]
    public int targetTomatoesForStats = 50;

    // Variables internas para estadísticas
    private int totalTomatoesCollected = 0;
    private bool timingStarted = false;
    private float startCollectionTime = 0f;
    private float endCollectionTime = 0f;
    private bool statsReported = false;

    private List<Vector2Int> currentPath = new();
    private int currentPathIndex = 0;

    // meta actual de la tarea en curso
    private Vector2Int currentGoal;
    private bool hasGoal = false;

    // flag para forzar replanteo de ruta
    private bool forceReplan = false;

    // Animación entre tiles
    private Vector3 worldFrom;
    private Vector3 worldTo;
    private float stepProgress = 1f; // 1 = llegó al destino

    // Posiciones clave
    private Vector2Int dsGridPos;
    private Vector2Int ecGridPos;
    private Vector2Int homeGridPos;

    // --- Sistema de tareas "reales" (TomatoTask) ---

    private List<TomatoFieldManager.TomatoTask> assignedTasks;
    private List<TomatoFieldManager.TomatoTask> pendingTasks = new();
    private List<TomatoFieldManager.TomatoTask> blockedTasks = new();
    private TomatoFieldManager.TomatoTask currentTask;

    private int carriedTomatoes = 0;
    private bool isBusy = false;

    // Costo aproximado restante (para prioridad de paso)
    public override float RemainingCost
    {
        get
        {
            int pathRemaining = 0;
            if (currentPath != null && currentPath.Count > 0)
                pathRemaining = currentPath.Count - currentPathIndex;

            int tasksRemaining = pendingTasks != null ? pendingTasks.Count : 0;

            return pathRemaining + tasksRemaining * 10;
        }
    }

    // ---------------- UNITY ----------------

    private void Start()
    {
        grid = GridManager.Instance;
        botManager = BotManager.Instance;

        // Get animator from child GameObject
        animator = GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning($"{name}: No Animator found in children!");
        }

        // posición inicial
        CurrentGridPos = grid.WorldToGrid(transform.position);
        transform.position = grid.GridToWorld(CurrentGridPos);

        // Fix Y position
        Vector3 pos = transform.position;
        pos.y = fixedYPosition;
        transform.position = pos;

        homeGridPos = CurrentGridPos;

        worldFrom = transform.position;
        worldTo = transform.position;
        stepProgress = 1f;

        botManager.RegisterBot(this, CurrentGridPos);

        if (dockingStationTransform != null)
            dsGridPos = grid.WorldToGrid(dockingStationTransform.position);
        if (ecTransform != null)
            ecGridPos = grid.WorldToGrid(ecTransform.position);

        // Seguridad: si el home no es walkable, usamos DS como home
        if (!grid.IsWalkable(homeGridPos) && dockingStationTransform != null)
        {
            Debug.LogWarning($"{name}: homeGridPos {homeGridPos} no es walkable. Usando DS como home.");
            homeGridPos = dsGridPos;
        }

        MissionComplete = false;
        missionStarted = false;

        // Modo productivo: espera a que el Orchestrator llame StartMission()
        state = BotState.GoingToDS_Initial;
        SetAnimationState(0); // idle hasta que empiece la misión
    }

    private void Update()
    {
        // Hasta que el Orchestrator no arranque la misión, no hacemos nada
        if (!missionStarted)
            return;

        if (isBusy)
            return;

        // animación entre tiles
        if (stepProgress < 1f)
        {
            float delta = Time.deltaTime * stepsPerSecond;
            stepProgress += delta;
            float t = Mathf.Clamp01(stepProgress);
            transform.position = Vector3.Lerp(worldFrom, worldTo, t);

            // Fix Y position during movement
            Vector3 pos = transform.position;
            pos.y = fixedYPosition;
            transform.position = pos;

            return;
        }

        // lógica de estaciones / tareas
        if (state == BotState.WaitingAtDS_Initial || state == BotState.WaitingAtDS_Final)
        {
            TryGoToDocking();
        }
        else if (state == BotState.WaitingForEC)
        {
            TryGoToEC();
        }
        else if (state == BotState.Harvesting &&
                 (currentPath == null || currentPath.Count == 0) &&
                 !hasGoal &&
                 pendingTasks != null &&
                 pendingTasks.Count > 0)
        {
            TryNextTask();
        }

        MoveAlongPath();
    }

    // ---------------- ANIMATION CONTROL ----------------

    private void SetAnimationState(int stateValue)
    {
        if (animator != null)
        {
            animator.SetInteger("State", stateValue);
        }
    }

    // ---------------- MOVIMIENTO ENTRE TILES ----------------

    private void MoveAlongPath()
    {
        if (forceReplan && hasGoal)
        {
            currentPath = grid.FindPath(CurrentGridPos, currentGoal, this);
            currentPathIndex = 0;
            forceReplan = false;
        }

        if (currentPath == null || currentPath.Count == 0) return;

        if (currentPathIndex >= currentPath.Count)
        {
            OnArrivedToDestination();
            return;
        }

        Vector2Int targetGridPos = currentPath[currentPathIndex];

        bool canMove = botManager.TryMoveWithPriority(this, CurrentGridPos, targetGridPos);
        if (!canMove)
        {
            return;
        }

        worldFrom = grid.GridToWorld(CurrentGridPos);
        CurrentGridPos = targetGridPos;
        worldTo = grid.GridToWorld(CurrentGridPos);

        // Update rotation to face movement direction
        Vector3 direction = worldTo - worldFrom;
        if (direction.sqrMagnitude > 0.01f)
        {
            direction.y = 0; // Keep rotation only on XZ plane
            Quaternion targetRotation = Quaternion.LookRotation(direction);
            transform.rotation = targetRotation;
        }

        // Fix Y position
        worldFrom.y = fixedYPosition;
        worldTo.y = fixedYPosition;

        stepProgress = 0f;
        currentPathIndex++;
    }

    private void OnArrivedToDestination()
    {
        hasGoal = false;
        currentPath.Clear();
        currentPathIndex = 0;

        switch (state)
        {
            case BotState.GoingToDS_Initial:
                StartCoroutine(WaitAtDockThenStartHarvest());
                break;

            case BotState.GoingToDS_Final:
                StartCoroutine(WaitAtDockThenGoHome());
                break;

            case BotState.ReturningHome:
                state = BotState.IdleFinished;
                MissionComplete = true;
                SetAnimationState(0);
                break;

            case BotState.GoingToEC:
                StartCoroutine(UnloadTomatoes());
                break;

            case BotState.Harvesting:
                if (currentTask != null &&
                    CurrentGridPos == currentTask.standPos)
                {
                    StartCoroutine(HarvestTomatoesAtCurrentTask());
                }
                else
                {
                    TryNextTask();
                }
                break;
        }
    }

    // ---------------- ESTACIONES: DS / EC ----------------

    private void TryGoToDocking()
    {
        // 1) Intentar ser el dueño de DS
        if (botManager.TryClaimDocking(this))
        {
            // Si estábamos en un slot de cola, lo liberamos
            botManager.ReleaseDockingQueueSlot(this);

            // Ir directamente al tile de DS
            if (state == BotState.WaitingAtDS_Initial)
                state = BotState.GoingToDS_Initial;
            else if (state == BotState.WaitingAtDS_Final)
                state = BotState.GoingToDS_Final;

            SetTargetInternal(dsGridPos);
            return;
        }

        // 2) No hay turno todavía -> intentar obtener un slot de cola físico
        if (botManager.TryGetDockingQueueSlot(this, out var queuePos))
        {
            if (CurrentGridPos != queuePos)
            {
                SetTargetInternal(queuePos);
            }
        }

        // 3) Estado pasa a "esperando en DS"
        if (state == BotState.GoingToDS_Initial)
            state = BotState.WaitingAtDS_Initial;
        else if (state == BotState.GoingToDS_Final)
            state = BotState.WaitingAtDS_Final;
    }

    private void TryGoToEC()
    {
        // 1) Intentar ser dueño de EC
        if (botManager.TryClaimEC(this))
        {
            botManager.ReleaseECQueueSlot(this);

            state = BotState.GoingToEC;
            SetTargetInternal(ecGridPos);
            return;
        }

        // 2) No hay turno -> pedir un slot de cola cerca de EC
        if (botManager.TryGetECQueueSlot(this, out var queuePos))
        {
            if (CurrentGridPos != queuePos)
            {
                SetTargetInternal(queuePos);
            }
        }

        // 3) Marcamos que estamos esperando en EC
        state = BotState.WaitingForEC;
    }

    // ---------------- TAREAS: SELECCIÓN Y RUTAS ----------------

    private TomatoFieldManager.TomatoTask PickClosestTask(
        List<TomatoFieldManager.TomatoTask> list,
        Vector2Int from)
    {
        TomatoFieldManager.TomatoTask best = null;
        int bestDist = int.MaxValue;

        foreach (var t in list)
        {
            int d = Mathf.Abs(t.standPos.x - from.x) + Mathf.Abs(t.standPos.y - from.y);
            if (d < bestDist)
            {
                bestDist = d;
                best = t;
            }
        }

        return best;
    }

    private void TryNextTask()
    {
        if (state != BotState.Harvesting)
            return;

        currentTask = null;

        if (pendingTasks == null)
        {
            currentPath.Clear();
            hasGoal = false;
            return;
        }

        if (pendingTasks.Count == 0)
        {
            if (blockedTasks != null && blockedTasks.Count > 0)
            {
                pendingTasks.AddRange(blockedTasks);
                blockedTasks.Clear();
            }
            else
            {
                // no hay más plantas
                if (carriedTomatoes > 0)
                {
                    TryGoToEC();
                }
                else
                {
                    state = BotState.GoingToDS_Final;
                    TryGoToDocking();
                }
                return;
            }
        }

        currentTask = PickClosestTask(pendingTasks, CurrentGridPos);
        pendingTasks.Remove(currentTask);

        Vector2Int targetGridPos = currentTask.standPos;

        bool ok = SetTargetInternal(targetGridPos);

        if (!ok)
        {
            blockedTasks.Add(currentTask);
            currentTask = null;
            TryNextTask();
        }
    }

    private bool SetTargetInternal(Vector2Int targetGridPos)
    {
        currentGoal = targetGridPos;
        hasGoal = true;

        currentPath = grid.FindPath(CurrentGridPos, targetGridPos, this);
        currentPathIndex = 0;

        if (currentPath == null || currentPath.Count == 0)
        {
            Debug.Log($"{name}: no se encontró camino hacia {targetGridPos}");
            hasGoal = false;
            return false;
        }

        return true;
    }

    public override void ForceReplan()
    {
        forceReplan = true;
    }

    /// <summary>
    /// Asigna las tareas (plantas) a este bot. El Orchestrator llama esto
    /// con la lista de plantas sanas que le corresponden.
    /// </summary>
    public void SetAssignedTasks(List<TomatoFieldManager.TomatoTask> tasks)
    {
        assignedTasks = tasks;
        pendingTasks = new List<TomatoFieldManager.TomatoTask>(tasks);
        blockedTasks = new List<TomatoFieldManager.TomatoTask>();
        currentTask = null;
    }

    /// <summary>
    /// Llamado por el Orchestrator cuando ya le asignó tareas
    /// y quiere que este pickbot arranque su misión.
    /// </summary>
    public void StartMission()
    {
        if (missionStarted)
            return;

        missionStarted = true;
        MissionComplete = false;

        state = BotState.GoingToDS_Initial;
        SetAnimationState(1);
        TryGoToDocking();
    }

    // ---------------- CORRUTINAS ----------------

    private IEnumerator WaitAtDockThenStartHarvest()
    {
        state = BotState.WaitingAtDS_Initial;
        isBusy = true;
        SetAnimationState(0);
        yield return new WaitForSeconds(dockWaitSeconds);
        SetAnimationState(1);
        yield return new WaitForSeconds(0.958f);
        isBusy = false;

        botManager.ReleaseDocking(this);

        state = BotState.Harvesting;
        TryNextTask();
    }

    private IEnumerator WaitAtDockThenGoHome()
    {
        state = BotState.WaitingAtDS_Final;
        isBusy = true;
        yield return new WaitForSeconds(dockWaitSeconds);
        isBusy = false;

        botManager.ReleaseDocking(this);

        state = BotState.ReturningHome;
        SetTargetInternal(homeGridPos);
    }

    private IEnumerator HarvestTomatoesAtCurrentTask()
    {
        if (currentTask == null)
            yield break;

        isBusy = true;

        while (true)
        {
            int availableCapacity = capacity - carriedTomatoes;

            if (availableCapacity <= 0)
            {
                if (currentTask.tomatoes > 0 &&
                    pendingTasks != null &&
                    !pendingTasks.Contains(currentTask))
                {
                    pendingTasks.Add(currentTask);
                }

                isBusy = false;
                TryGoToEC();      // mochila llena -> ir a EC
                currentTask = null;
                yield break;
            }

            if (currentTask.tomatoes <= 0)
            {
                break;
            }

            // Simular tiempo de recolección de un solo tomate
            yield return new WaitForSeconds(perTomatoPickupTime);

            // Cargar uno a la mochila
            carriedTomatoes++;

            // ---- Lógica de estadísticas de recolección ----
            totalTomatoesCollected++;

            if (!timingStarted)
            {
                timingStarted = true;
                startCollectionTime = Time.time;
            }

            if (!statsReported && totalTomatoesCollected >= targetTomatoesForStats)
            {
                statsReported = true;
                endCollectionTime = Time.time;
                float elapsed = endCollectionTime - startCollectionTime;

                Debug.Log($"[Stats] Bot '{name}' recolectó {targetTomatoesForStats} tomates en {elapsed:F2} segundos.");

                if (botManager != null)
                {
                    botManager.ReportBotFinished(elapsed);
                }
            }
            // ---- Fin lógica de estadísticas ----

            // Quitar visual + bajar conteo lógico
            if (TomatoFieldManager.Instance != null)
            {
                TomatoFieldManager.Instance.ConsumeTomato(currentTask);
            }
            else
            {
                currentTask.tomatoes = Mathf.Max(0, currentTask.tomatoes - 1);
            }

            if (carriedTomatoes >= capacity)
            {
                if (currentTask.tomatoes > 0 &&
                    pendingTasks != null &&
                    !pendingTasks.Contains(currentTask))
                {
                    pendingTasks.Add(currentTask);
                }

                isBusy = false;
                TryGoToEC();
                currentTask = null;
                yield break;
            }
        }

        isBusy = false;

        currentTask = null;

        if (carriedTomatoes > 0 && carriedTomatoes < capacity)
        {
            TryNextTask();
        }
        else if (carriedTomatoes >= capacity)
        {
            TryGoToEC();
        }
        else
        {
            TryNextTask();
        }
    }

    private IEnumerator UnloadTomatoes()
    {
        if (carriedTomatoes <= 0)
        {
            state = BotState.Harvesting;
            TryNextTask();
            yield break;
        }

        state = BotState.Unloading;
        isBusy = true;

        // Set animation to idle while unloading
        SetAnimationState(0);

        int toDrop = carriedTomatoes;
        for (int i = 0; i < toDrop; i++)
        {
            yield return new WaitForSeconds(perTomatoDropTime);
            carriedTomatoes--;
            // aquí luego ponemos bolita visual en EC
        }

        isBusy = false;
        botManager.ReleaseEC(this);

        if ((pendingTasks != null && pendingTasks.Count > 0) ||
            (blockedTasks != null && blockedTasks.Count > 0))
        {
            state = BotState.Harvesting;
            SetAnimationState(1);
            yield return new WaitForSeconds(0.958f);
            TryNextTask();
        }
        else
        {
            state = BotState.GoingToDS_Final;
            TryGoToDocking();
        }
    }

    // ---------------- GIZMOS ----------------

    private void OnDrawGizmosSelected()
    {
        if (grid == null) grid = GridManager.Instance;

        if (assignedTasks != null)
        {
            Gizmos.color = Color.cyan;
            for (int i = 0; i < assignedTasks.Count; i++)
            {
                Vector3 p = grid.GridToWorld(assignedTasks[i].standPos) + Vector3.up * 0.1f;
                Gizmos.DrawCube(p, Vector3.one * 0.3f);
            }
        }

        if (currentPath != null && currentPath.Count > 0)
        {
            Gizmos.color = Color.yellow;
            Vector3 prev = transform.position;

            for (int i = currentPathIndex; i < currentPath.Count; i++)
            {
                Vector3 p = grid.GridToWorld(currentPath[i]) + Vector3.up * 0.05f;
                Gizmos.DrawSphere(p, 0.1f);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
