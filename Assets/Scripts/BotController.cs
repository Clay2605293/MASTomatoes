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

        // Get home tile from parent's PositionToHomeTile component
        Transform homeTransform = GetHomeTileFromParent();
        
        // Home: si hay homeTransform, usarlo; si no, usar posición inicial
        if (homeTransform != null)
        {
            homeGridPos = grid.WorldToGrid(homeTransform.position);
            
            // Align bot's X and Z position to home tile
            Vector3 alignedPosition = transform.position;
            alignedPosition.x = homeTransform.position.x;
            alignedPosition.z = homeTransform.position.z;
            transform.position = alignedPosition;
        }
        else
        {
            homeGridPos = CurrentGridPos;
        }

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

    /// <summary>
    /// Gets the home tile Transform from the parent's PositionToHomeTile component
    /// </summary>
    private Transform GetHomeTileFromParent()
    {
        if (transform.parent == null)
        {
            Debug.LogWarning($"{name}: No parent object found. Cannot inherit home tile.");
            return null;
        }

        PositionToHomeTile positionScript = transform.parent.GetComponent<PositionToHomeTile>();
        if (positionScript == null)
        {
            Debug.LogWarning($"{name}: Parent object does not have PositionToHomeTile component. Cannot inherit home tile.");
            return null;
        }

        if (positionScript.homeTile == null)
        {
            Debug.LogWarning($"{name}: Parent's PositionToHomeTile.homeTile is not assigned!");
            return null;
        }

        return positionScript.homeTile.transform;
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

        worldFrom.y = fixedYPosition;
        worldTo.y = fixedYPosition;
        stepProgress = 0f;
        currentPathIndex++;

        SetAnimationState(1);
    }

    private void OnArrivedToDestination()
    {
        hasGoal = false;
        currentPath.Clear();
        currentPathIndex = 0;

        switch (state)
        {
            case BotState.GoingToDS_Initial:
                if (CurrentGridPos == dsGridPos)
                    StartCoroutine(WaitAtDockThenStartHarvest());
                break;

            case BotState.GoingToDS_Final:
                if (CurrentGridPos == dsGridPos)
                    StartCoroutine(WaitAtDockThenGoHome());
                break;

            case BotState.Harvesting:
                if (currentTask != null &&
                    CurrentGridPos == currentTask.standPos &&
                    !isBusy &&
                    carriedTomatoes < capacity)
                {
                    StartCoroutine(HarvestTomatoesAtCurrentTask());
                }
                break;

            case BotState.GoingToEC:
                if (CurrentGridPos == ecGridPos)
                    StartCoroutine(UnloadTomatoes());
                break;

            case BotState.ReturningHome:
                if (CurrentGridPos == homeGridPos)
                {
                    state = BotState.IdleFinished;
                    SetAnimationState(0);
                    MissionComplete = true;
                    Debug.Log($"{name}: Llegó a home y finalizó misión.");
                }
                else
                {
                    Debug.LogWarning($"{name}: Camino a home acabó en {CurrentGridPos}, pero home es {homeGridPos}.");
                    SetTargetInternal(homeGridPos);
                }
                break;
        }
    }

    private void TryGoToDocking()
    {
        if (botManager.TryClaimDocking(this))
        {
            botManager.ReleaseDockingQueueSlot(this);

            if (state == BotState.WaitingAtDS_Initial)
                state = BotState.GoingToDS_Initial;
            else if (state == BotState.WaitingAtDS_Final)
                state = BotState.GoingToDS_Final;

            SetTargetInternal(dsGridPos);
            return;
        }

        if (botManager.TryGetDockingQueueSlot(this, out var queuePos))
        {
            if (CurrentGridPos != queuePos)
            {
                SetTargetInternal(queuePos);
            }
        }

        if (state == BotState.GoingToDS_Initial)
            state = BotState.WaitingAtDS_Initial;
        else if (state == BotState.GoingToDS_Final)
            state = BotState.WaitingAtDS_Final;
    }

    private void TryGoToEC()
    {
        if (carriedTomatoes <= 0 && state != BotState.Unloading)
        {
            state = BotState.Harvesting;
            TryNextTask();
            return;
        }

        if (botManager.TryClaimEC(this))
        {
            state = BotState.GoingToEC;
            SetTargetInternal(ecGridPos);
            return;
        }

        state = BotState.WaitingForEC;
    }

    private void TryNextTask()
    {
        if ((pendingTasks == null || pendingTasks.Count == 0) &&
            (blockedTasks == null || blockedTasks.Count == 0))
        {
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

        if (pendingTasks != null && pendingTasks.Count > 0)
        {
            var closest = PickClosestTask(pendingTasks, CurrentGridPos);
            if (closest == null)
            {
                MovePendingToBlocked();
            }
            else
            {
                pendingTasks.Remove(closest);
                currentTask = closest;

                if (carriedTomatoes >= capacity)
                {
                    pendingTasks.Add(closest);
                    TryGoToEC();
                    return;
                }

                SetTargetInternal(closest.standPos);
                return;
            }
        }

        if (blockedTasks != null && blockedTasks.Count > 0)
        {
            var oldest = blockedTasks[0];
            blockedTasks.RemoveAt(0);
            currentTask = oldest;

            if (carriedTomatoes >= capacity)
            {
                blockedTasks.Add(oldest);
                TryGoToEC();
                return;
            }

            SetTargetInternal(oldest.standPos);
            return;
        }
    }

    private void MovePendingToBlocked()
    {
        if (pendingTasks == null || pendingTasks.Count == 0)
            return;

        if (blockedTasks == null)
            blockedTasks = new List<TomatoFieldManager.TomatoTask>();

        blockedTasks.AddRange(pendingTasks);
        pendingTasks.Clear();
    }

    private TomatoFieldManager.TomatoTask PickClosestTask(List<TomatoFieldManager.TomatoTask> tasks, Vector2Int fromPos)
    {
        TomatoFieldManager.TomatoTask best = null;
        int bestDist = int.MaxValue;

        for (int i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            var sp = t.standPos;

            var pathToTask = grid.FindPath(fromPos, sp, this);
            if (pathToTask == null || pathToTask.Count == 0)
                continue;

            int dist = pathToTask.Count;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = t;
            }
        }

        return best;
    }

    private bool SetTargetInternal(Vector2Int targetGridPos)
    {
        if (targetGridPos == CurrentGridPos)
        {
            OnArrivedToDestination();
            return false;
        }

        currentGoal = targetGridPos;
        hasGoal = true;

        currentPath = grid.FindPath(CurrentGridPos, targetGridPos, this);
        currentPathIndex = 0;

        if (currentPath == null || currentPath.Count == 0)
        {
            Debug.LogWarning($"{name}: no se encontró camino hacia {targetGridPos} (estado: {state})");
            hasGoal = false;

            // Salvavidas: si no hay camino a home estando en ReturningHome,
            // no dejamos colgado al orquestador.
            if (state == BotState.ReturningHome)
            {
                state = BotState.IdleFinished;
                MissionComplete = true;
                SetAnimationState(0);
                Debug.LogWarning($"{name}: no pude llegar a home, marco MissionComplete igualmente.");
            }

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
