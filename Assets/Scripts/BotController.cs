using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BotController : MonoBehaviour
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

    public float moveSpeed = 3f; // no se usa aún, lo dejamos por si luego cambiamos el modelo

    [Header("Velocidad en tiles")]
    public float stepsPerSecond = 2f; // cuántos tiles por segundo recorre

    [Header("Debug / Órdenes")]
    public Transform debugTarget;          // destino único de prueba (opcional)
    public List<Transform> taskTargets;    // lista de destinos (modo debug)

    [Header("Config")]
    public bool useInspectorTasks = false; // si true, usa taskTargets/debugTarget

    [Header("Stations")]
    public Transform dockingStationTransform; // DS
    public Transform ecTransform;             // EC

    [Header("Tomates")]
    public int capacity = 5;
    public float perTomatoPickupTime = 0.1f;
    public float perTomatoDropTime = 0.1f;
    public float dockWaitSeconds = 2f;

    // ---------------- CAMPOS PRIVADOS ----------------

    private GridManager grid;
    private BotManager botManager;

    private List<Vector2Int> currentPath = new();
    private int currentPathIndex = 0;

    private Queue<Vector2Int> taskQueue = new(); // solo modo debug

    // posición actual en coordenadas de grilla
    public Vector2Int CurrentGridPos { get; private set; }

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
    public float RemainingCost
    {
        get
        {
            int pathRemaining = 0;
            if (currentPath != null && currentPath.Count > 0)
                pathRemaining = currentPath.Count - currentPathIndex;

            int tasksRemaining;
            if (useInspectorTasks)
            {
                tasksRemaining = taskQueue.Count;
            }
            else
            {
                tasksRemaining = pendingTasks != null ? pendingTasks.Count : 0;
            }

            return pathRemaining + tasksRemaining * 10;
        }
    }

    // ---------------- UNITY ----------------

    private void Start()
    {
        grid = GridManager.Instance;
        botManager = BotManager.Instance;

        // posición inicial
        CurrentGridPos = grid.WorldToGrid(transform.position);
        transform.position = grid.GridToWorld(CurrentGridPos);
        homeGridPos = CurrentGridPos;

        worldFrom = transform.position;
        worldTo = transform.position;
        stepProgress = 1f;

        botManager.RegisterBot(this, CurrentGridPos);

        if (dockingStationTransform != null)
            dsGridPos = grid.WorldToGrid(dockingStationTransform.position);
        if (ecTransform != null)
            ecGridPos = grid.WorldToGrid(ecTransform.position);

        if (useInspectorTasks)
        {
            InitDebugTasks();
        }
        else
        {
            state = BotState.GoingToDS_Initial;
            TryGoToDocking(); // pedimos turno para DS inicial
        }
    }

    private void Update()
    {
        if (isBusy)
            return;

        // animación entre tiles
        if (stepProgress < 1f)
        {
            float delta = Time.deltaTime * stepsPerSecond;
            stepProgress += delta;
            float t = Mathf.Clamp01(stepProgress);
            transform.position = Vector3.Lerp(worldFrom, worldTo, t);
            return;
        }

        if (useInspectorTasks)
        {
            if ((currentPath == null || currentPath.Count == 0) &&
                taskQueue.Count > 0 &&
                !hasGoal)
            {
                TryNextTask();
            }
        }
        else
        {
            // si estamos esperando DS, intentamos reclamarla
            if (state == BotState.WaitingAtDS_Initial || state == BotState.WaitingAtDS_Final)
            {
                TryGoToDocking();
            }
            // si estamos esperando EC, intentamos reclamarla
            else if (state == BotState.WaitingForEC)
            {
                TryGoToEC();
            }
            // si estamos cosechando y ya no tenemos ruta ni meta, elegimos nueva planta
            else if (state == BotState.Harvesting &&
                     (currentPath == null || currentPath.Count == 0) &&
                     !hasGoal &&
                     pendingTasks != null &&
                     pendingTasks.Count > 0)
            {
                TryNextTask();
            }
        }

        MoveAlongPath();
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

        stepProgress = 0f;
        currentPathIndex++;
    }

    private void OnArrivedToDestination()
    {
        hasGoal = false;
        currentPath.Clear();
        currentPathIndex = 0;

        if (useInspectorTasks)
        {
            TryNextTask();
            return;
        }

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

    private void InitDebugTasks()
    {
        if (taskTargets != null && taskTargets.Count > 0)
        {
            foreach (var t in taskTargets)
            {
                if (t == null) continue;
                Vector2Int pos = grid.WorldToGrid(t.position);
                if (grid.IsWalkable(pos))
                {
                    taskQueue.Enqueue(pos);
                }
                else
                {
                    Debug.LogWarning($"{name}: task {t.name} no está sobre un tile caminable");
                }
            }
        }
        else if (debugTarget != null)
        {
            Vector2Int targetGridPos = grid.WorldToGrid(debugTarget.position);
            if (grid.IsWalkable(targetGridPos))
            {
                taskQueue.Enqueue(targetGridPos);
            }
            else
            {
                Debug.LogWarning($"{name}: debugTarget no está sobre un tile caminable");
            }
        }

        TryNextTask();
    }

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
        if (useInspectorTasks)
        {
            if (taskQueue.Count == 0)
            {
                currentPath.Clear();
                hasGoal = false;
                return;
            }

            Vector2Int nextTarget = taskQueue.Dequeue();
            SetTargetInternal(nextTarget);
            return;
        }

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

    public void SetTarget(Vector2Int targetGridPos)
    {
        SetTargetInternal(targetGridPos);
    }

    public void ForceReplan()
    {
        forceReplan = true;
    }

    public void SetAssignedTasks(List<TomatoFieldManager.TomatoTask> tasks)
    {
        assignedTasks = tasks;
        pendingTasks = new List<TomatoFieldManager.TomatoTask>(tasks);
        blockedTasks = new List<TomatoFieldManager.TomatoTask>();
        currentTask = null;

        taskQueue.Clear();
    }

    // ---------------- CORRUTINAS ----------------

    private IEnumerator WaitAtDockThenStartHarvest()
    {
        state = BotState.WaitingAtDS_Initial;
        isBusy = true;
        yield return new WaitForSeconds(dockWaitSeconds);
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

        // Vamos a cosechar esta planta tantas veces como podamos
        // hasta que:
        //  - se nos llene la mochila, o
        //  - se terminen los tomates de esta planta.
        isBusy = true;

        while (true)
        {
            int availableCapacity = capacity - carriedTomatoes;

            // Sin capacidad antes de tomar algo
            if (availableCapacity <= 0)
            {
                // Si todavía hay tomates en esta planta, la volvemos a poner
                // en la lista de pendientes para regresar más tarde.
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

            // Si ya no quedan tomates aquí, salimos.
            if (currentTask.tomatoes <= 0)
            {
                break;
            }

            // Simular tiempo de recolección de un solo tomate
            yield return new WaitForSeconds(perTomatoPickupTime);

            // Cargar uno a la mochila
            carriedTomatoes++;

            // Quitar visual + bajar conteo lógico
            if (TomatoFieldManager.Instance != null)
            {
                TomatoFieldManager.Instance.ConsumeTomato(currentTask);
            }
            else
            {
                // Fallback por si algo raro pasa con el manager
                currentTask.tomatoes = Mathf.Max(0, currentTask.tomatoes - 1);
            }

            // Por si en este mismo tomate llegamos a la capacidad
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

        // Llegamos aquí porque se acabaron los tomates de esta planta
        isBusy = false;

        // Ya no la re-encolamos porque tomatoes == 0
        currentTask = null;

        // Si aun tenemos capacidad, seguimos con otra planta.
        // Si no, igualmente vamos a EC (por seguridad).
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
            // Caso borde: no tomamos nada (planta ya sin tomates)
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
