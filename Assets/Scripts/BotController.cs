using System.Collections.Generic;
using UnityEngine;

public class BotController : MonoBehaviour
{
    public float moveSpeed = 3f; // no se usa aún, lo dejamos por si luego cambiamos el modelo

    [Header("Velocidad en tiles")]
    public float stepsPerSecond = 2f; // cuántos tiles por segundo recorre

    [Header("Debug / Órdenes")]
    public Transform debugTarget;          // destino único de prueba (opcional)
    public List<Transform> taskTargets;    // lista de destinos (tomates, zonas, etc.)

    [Header("Config")]
    public bool useInspectorTasks = false; // si true, usa taskTargets/debugTarget

    private GridManager grid;
    private BotManager botManager;

    private List<Vector2Int> currentPath = new List<Vector2Int>();
    private int currentPathIndex = 0;

    // Cola solo para modo debug (useInspectorTasks)
    private Queue<Vector2Int> taskQueue = new Queue<Vector2Int>();

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
    // 1 = llegó al destino del paso; 0 = inicio del paso
    private float stepProgress = 1f;

    // --- Sistema de tareas "reales" (TomatoTask) ---

    // Lista completa de tareas que el TaskDistributor le asignó a este bot
    private List<TomatoFieldManager.TomatoTask> assignedTasks;

    // Subconjuntos de trabajo
    private List<TomatoFieldManager.TomatoTask> pendingTasks = new();
    private List<TomatoFieldManager.TomatoTask> blockedTasks = new();

    // Tarea actual que está intentando ejecutar
    private TomatoFieldManager.TomatoTask currentTask;

    // Costo aproximado restante (para prioridad)
    // MENOR costo => más prioridad
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

    private void Start()
    {
        grid = GridManager.Instance;
        botManager = BotManager.Instance;

        // calcular y alinear posición inicial al grid
        CurrentGridPos = grid.WorldToGrid(transform.position);
        transform.position = grid.GridToWorld(CurrentGridPos);

        worldFrom = transform.position;
        worldTo = transform.position;
        stepProgress = 1f; // empieza quieto

        // registrar al bot en el manager
        botManager.RegisterBot(this, CurrentGridPos);

        // SOLO si queremos probar con targets del inspector
        if (useInspectorTasks)
        {
            // 1) Cargar tareas desde el inspector
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
            // 2) Si no hay lista de tareas, usamos el debugTarget
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
    }

    private void Update()
    {
        // 1) Si estamos a medio paso, solo animamos el movimiento entre tiles
        if (stepProgress < 1f)
        {
            float delta = Time.deltaTime * stepsPerSecond; // tiles/seg
            stepProgress += delta;
            float t = Mathf.Clamp01(stepProgress);
            transform.position = Vector3.Lerp(worldFrom, worldTo, t);
            return;
        }

        // 2) Si ya terminamos el paso anterior, decidir siguiente tarea si hace falta
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
            if ((currentPath == null || currentPath.Count == 0) &&
                !hasGoal &&
                pendingTasks != null &&
                pendingTasks.Count > 0)
            {
                TryNextTask();
            }
        }

        MoveAlongPath();
    }

    private void MoveAlongPath()
    {
        // ¿hay que replantear la ruta hacia la misma meta?
        if (forceReplan && hasGoal)
        {
            // OJO: tu GridManager debe tener esta versión de FindPath(start, goal, bot)
            currentPath = grid.FindPath(CurrentGridPos, currentGoal, this);
            currentPathIndex = 0;
            forceReplan = false;
        }

        if (currentPath == null || currentPath.Count == 0) return;
        if (currentPathIndex >= currentPath.Count)
        {
            // ya llegamos a la meta actual -> siguiente tarea
            TryNextTask();
            return;
        }

        Vector2Int targetGridPos = currentPath[currentPathIndex];

        // pedimos permiso al BotManager, que decide con prioridad por RemainingCost
        bool canMove = botManager.TryMoveWithPriority(this, CurrentGridPos, targetGridPos);
        if (!canMove)
        {
            // este "tick" lógico no nos movemos; ya veremos en el siguiente
            return;
        }

        // movimiento por tiles: preparamos animación del paso
        worldFrom = grid.GridToWorld(CurrentGridPos);
        CurrentGridPos = targetGridPos; // lógicamente ya estamos en el nuevo tile
        worldTo = grid.GridToWorld(CurrentGridPos);

        stepProgress = 0f; // empezamos a animar de from -> to

        currentPathIndex++;
    }

    // Elige la tarea más cercana (distancia Manhattan) de una lista
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
        // --- MODO DEBUG: usa cola de Vector2Int ---
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

        // --- MODO NORMAL: usa TomatoTask asignadas ---
        currentTask = null;

        if (pendingTasks == null)
        {
            currentPath.Clear();
            hasGoal = false;
            return;
        }

        // Si ya no hay pendientes pero sí bloqueadas, las reintentamos
        if (pendingTasks.Count == 0)
        {
            if (blockedTasks != null && blockedTasks.Count > 0)
            {
                pendingTasks.AddRange(blockedTasks);
                blockedTasks.Clear();
            }
            else
            {
                currentPath.Clear();
                hasGoal = false;
                return;
            }
        }

        // Elegimos la tarea más cercana a la posición actual
        currentTask = PickClosestTask(pendingTasks, CurrentGridPos);
        pendingTasks.Remove(currentTask);

        Vector2Int targetGridPos = currentTask.standPos;

        // Intentamos trazar un camino
        bool ok = SetTargetInternal(targetGridPos);

        if (!ok)
        {
            // No hay camino: la mandamos a la lista de bloqueadas
            blockedTasks.Add(currentTask);
            currentTask = null;

            // Intentar con otra tarea
            TryNextTask();
        }
    }

    // Versión interna que devuelve true/false si hay camino
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

    // Versión pública por si algún otro script la quiere usar "a la antigua"
    public void SetTarget(Vector2Int targetGridPos)
    {
        SetTargetInternal(targetGridPos);
    }

    // llamado por BotManager cuando otro bot nos "gana" la celda
    public void ForceReplan()
    {
        // en el siguiente "tick lógico" se recalculará la ruta hacia currentGoal
        forceReplan = true;
    }

    // Llamado por TaskDistributor para darle sus tareas a este bot
    public void SetAssignedTasks(List<TomatoFieldManager.TomatoTask> tasks)
    {
        assignedTasks = tasks;

        // Creamos las listas de trabajo
        pendingTasks = new List<TomatoFieldManager.TomatoTask>(tasks);
        blockedTasks = new List<TomatoFieldManager.TomatoTask>();
        currentTask = null;

        // limpiamos cualquier queue anterior de modo debug
        taskQueue.Clear();
    }

    private void OnDrawGizmosSelected()
    {
        if (grid == null) grid = GridManager.Instance;

        // Tareas asignadas al bot
        if (assignedTasks != null)
        {
            Gizmos.color = Color.cyan;
            for (int i = 0; i < assignedTasks.Count; i++)
            {
                Vector3 p = grid.GridToWorld(assignedTasks[i].standPos) + Vector3.up * 0.1f;
                Gizmos.DrawCube(p, Vector3.one * 0.3f);
            }
        }

        // Camino actual
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
