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

    private GridManager grid;
    private BotManager botManager;

    private List<Vector2Int> currentPath = new List<Vector2Int>();
    private int currentPathIndex = 0;

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

    // Costo aproximado restante (para prioridad)
    // MENOR costo => más prioridad
    public float RemainingCost
    {
        get
        {
            int pathRemaining = 0;
            if (currentPath != null && currentPath.Count > 0)
                pathRemaining = currentPath.Count - currentPathIndex;

            // peso arbitrario para tareas pendientes
            int tasksRemaining = taskQueue.Count * 10;

            return pathRemaining + tasksRemaining;
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

    private void Update()
    {
        // 1) Si estamos a medio paso, solo animamos el movimiento entre tiles
        if (stepProgress < 1f)
        {
            float delta = Time.deltaTime * stepsPerSecond; // stepsPerSecond = tiles/seg
            stepProgress += delta;
            float t = Mathf.Clamp01(stepProgress);
            transform.position = Vector3.Lerp(worldFrom, worldTo, t);
            return;
        }

        // 2) Si ya terminamos el paso anterior, podemos decidir el siguiente
        MoveAlongPath();
    }

    private void MoveAlongPath()
    {
        // ¿hay que replantear la ruta hacia la misma meta?
        if (forceReplan && hasGoal)
        {
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

    private void TryNextTask()
    {
        if (taskQueue.Count == 0)
        {
            // no hay más tareas
            currentPath.Clear();
            hasGoal = false;
            return;
        }

        Vector2Int nextTarget = taskQueue.Dequeue();
        SetTarget(nextTarget);
    }

    public void SetTarget(Vector2Int targetGridPos)
    {
        currentGoal = targetGridPos;
        hasGoal = true;

        currentPath = grid.FindPath(CurrentGridPos, targetGridPos, this);
        currentPathIndex = 0;

        if (currentPath.Count == 0)
        {
            Debug.Log($"{name}: no se encontró camino hacia {targetGridPos}");
        }
    }

    // llamado por BotManager cuando otro bot nos "gana" la celda
    public void ForceReplan()
    {
        // en el siguiente "tick lógico" se recalculará la ruta hacia currentGoal
        forceReplan = true;
    }
}
