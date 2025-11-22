using System.Collections.Generic;
using UnityEngine;

public class BotController : MonoBehaviour
{
    public float moveSpeed = 3f;

    // NUEVO: un objeto en la escena que marque el destino de prueba
    public Transform debugTarget;

    private GridManager grid;
    private List<Vector2Int> currentPath = new List<Vector2Int>();
    private int currentPathIndex = 0;

    public Vector2Int CurrentGridPos { get; private set; }

    private void Start()
    {
        grid = GridManager.Instance;

        CurrentGridPos = grid.WorldToGrid(transform.position);
        transform.position = grid.GridToWorld(CurrentGridPos);

        if (debugTarget != null)
        {
            Vector2Int targetGridPos = grid.WorldToGrid(debugTarget.position);
            Debug.Log($"{name}: start {CurrentGridPos}, target {targetGridPos}, " +
                    $"startWalkable={grid.IsWalkable(CurrentGridPos)}, targetWalkable={grid.IsWalkable(targetGridPos)}");

            if (grid.IsWalkable(targetGridPos))
            {
                SetTarget(targetGridPos);
            }
            else
            {
                Debug.LogWarning($"{name}: el debugTarget no está sobre un tile caminable");
            }
        }
    }


    private void Update()
    {
        MoveAlongPath();
    }

    private void MoveAlongPath()
    {
        if (currentPath == null || currentPath.Count == 0) return;
        if (currentPathIndex >= currentPath.Count) return;

        Vector2Int targetGridPos = currentPath[currentPathIndex];
        Vector3 targetWorldPos = grid.GridToWorld(targetGridPos);

        transform.position = Vector3.MoveTowards(
            transform.position,
            targetWorldPos,
            moveSpeed * Time.deltaTime
        );

        if (Vector3.Distance(transform.position, targetWorldPos) < 0.01f)
        {
            CurrentGridPos = targetGridPos;
            currentPathIndex++;
        }
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
