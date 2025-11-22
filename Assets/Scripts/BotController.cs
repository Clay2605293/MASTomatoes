using System.Collections.Generic;
using UnityEngine;

public class BotController : MonoBehaviour
{
    public float moveSpeed = 3f;
    private BotManager botManager;

    [Header("Debug / Ordenes")]
    public Transform debugTarget;          // ya existente
    public List<Transform> taskTargets;    // NUEVO: lista de destinos (tomates)

    private GridManager grid;
    private List<Vector2Int> currentPath = new List<Vector2Int>();
    private int currentPathIndex = 0;

    private Queue<Vector2Int> taskQueue = new Queue<Vector2Int>();

    public Vector2Int CurrentGridPos { get; private set; }

    private void Start()
    {
        grid = GridManager.Instance;
        botManager = BotManager.Instance;

        CurrentGridPos = grid.WorldToGrid(transform.position);
        transform.position = grid.GridToWorld(CurrentGridPos);

        // registrar posición inicial
        botManager.RegisterBot(this, CurrentGridPos);

        // 1) Si hay tareas en la lista del inspector, las cargamos a la cola
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
                    Debug.LogWarning($"{name}: task {t.name} no está en tile caminable");
                }
            }
        }
        // 2) Si no hay tareas, usamos el debugTarget (como antes)
        else if (debugTarget != null)
        {
            Vector2Int targetGridPos = grid.WorldToGrid(debugTarget.position);
            if (grid.IsWalkable(targetGridPos))
            {
                taskQueue.Enqueue(targetGridPos);
            }
        }

        TryNextTask();
    }

    private void Update()
    {
        MoveAlongPath();
    }

    private void MoveAlongPath()
        {
            if (currentPath == null || currentPath.Count == 0) return;
            if (currentPathIndex >= currentPath.Count)
            {
                TryNextTask();
                return;
            }

            Vector2Int targetGridPos = currentPath[currentPathIndex];

            // 1) si la siguiente celda está ocupada por otro bot, nos esperamos
            if (!botManager.IsCellFree(targetGridPos, this))
            {
                // aquí podrías meter lógica de "ceder el paso"; por ahora solo esperar
                return;
            }

            Vector3 targetWorldPos = grid.GridToWorld(targetGridPos);

            transform.position = Vector3.MoveTowards(
                transform.position,
                targetWorldPos,
                moveSpeed * Time.deltaTime
            );

            if (Vector3.Distance(transform.position, targetWorldPos) < 0.01f)
            {
                // 2) actualizamos en el manager la celda ocupada
                Vector2Int oldPos = CurrentGridPos;
                CurrentGridPos = targetGridPos;
                botManager.UpdateBotPosition(this, oldPos, CurrentGridPos);

                currentPathIndex++;
            }
        }


    private void TryNextTask()
    {
        if (taskQueue.Count == 0)
        {
            currentPath.Clear();
            return;
        }

        Vector2Int nextTarget = taskQueue.Dequeue();
        SetTarget(nextTarget);
    }

    public void SetTarget(Vector2Int targetGridPos)
    {
        currentPath = grid.FindPath(CurrentGridPos, targetGridPos);
        currentPathIndex = 0;

        if (currentPath.Count == 0)
        {
            Debug.Log($"{name}: no se encontró camino hacia {targetGridPos}");
        }
    }
}
