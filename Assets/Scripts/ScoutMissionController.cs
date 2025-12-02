using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Distributes inspection routes among all available ScoutBotBrain agents.
/// Slices the map into vertical strips based on X coordinates (same idea as antes).
/// Cada scout recibe una lista de tiles (gridPos) frente a plantas que debe inspeccionar.
/// </summary>
public class ScoutMissionController : MonoBehaviour
{
    [Tooltip("If true, distributes tasks automatically on Start")]
    public bool distributeOnStart = true;

    [Tooltip("Delay before distribution to ensure all grids/bots are initialized")]
    public float startDelay = 1.0f;

    private void Start()
    {
        if (distributeOnStart)
        {
            StartCoroutine(DistributeRoutine());
        }
    }

    private System.Collections.IEnumerator DistributeRoutine()
    {
        // Esperamos un poco para que GridManager, TomatoFieldManager y bots estén listos
        yield return new WaitForSeconds(startDelay);
        DistributeTasks();
    }

    [ContextMenu("Distribute Tasks")]
    public void DistributeTasks()
    {
        // 1. Buscar todos los nuevos Scouts (ScoutBotBrain)
        ScoutBotBrain[] scouts = FindObjectsOfType<ScoutBotBrain>();
        if (scouts.Length == 0)
        {
            Debug.LogWarning("[MissionController] No ScoutBotBrain found.");
            return;
        }

        // Ordenar por nombre para tener asignación determinista (Scout 1, Scout 2, etc.)
        System.Array.Sort(scouts, (a, b) => string.Compare(a.name, b.name));

        Debug.Log($"[MissionController] Found {scouts.Length} scouts. Preparing distribution...");

        // 2. Encontrar todos los Harvest Spots (tiles frente a plantas)
        TileInfo[] allTiles = FindObjectsOfType<TileInfo>();
        List<Vector2Int> harvestSpots = new List<Vector2Int>();

        foreach (var tile in allTiles)
        {
            if (tile.isHarvestSpot && tile.walkable)
            {
                harvestSpots.Add(tile.gridPos);
            }
        }

        if (harvestSpots.Count == 0)
        {
            Debug.LogWarning("[MissionController] No harvest spots found.");
            return;
        }

        // 3. Ordenar spots por X (estrategia de franjas verticales)
        // De esta forma, Scout 1 inspecciona la franja izquierda, Scout 2 la siguiente, etc.
        harvestSpots.Sort((a, b) =>
        {
            int xComp = a.x.CompareTo(b.x);
            if (xComp != 0) return xComp;
            return a.y.CompareTo(b.y);
        });

        Debug.Log($"[MissionController] Found {harvestSpots.Count} harvest spots. Distributing...");

        // 4. Dividir y asignar de forma equitativa
        int totalSpots = harvestSpots.Count;
        int numScouts = scouts.Length;

        int baseCount = totalSpots / numScouts;
        int remainder = totalSpots % numScouts;
        int currentIndex = 0;

        for (int i = 0; i < numScouts; i++)
        {
            int count = baseCount + (i < remainder ? 1 : 0);

            if (count <= 0)
            {
                Debug.LogWarning($"[MissionController] Scout {scouts[i].name} got 0 spots (too many scouts?)");
                scouts[i].AssignRoute(new List<Vector2Int>());
                continue;
            }

            List<Vector2Int> assignedSpots = harvestSpots.GetRange(currentIndex, count);
            currentIndex += count;

            Debug.Log($"[MissionController] Assigning {assignedSpots.Count} spots to {scouts[i].name} (Range X: {assignedSpots[0].x} to {assignedSpots[assignedSpots.Count - 1].x})");

            // Asignar la ruta a este scout
            scouts[i].AssignRoute(assignedSpots);
        }
    }
}
