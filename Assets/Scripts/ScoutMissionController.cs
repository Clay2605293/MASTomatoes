using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Distributes harvest tasks among all available ScoutBots.
/// Slices the map into vertical strips based on X coordinates.
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
        yield return new WaitForSeconds(startDelay);
        DistributeTasks();
    }

    [ContextMenu("Distribute Tasks")]
    public void DistributeTasks()
    {
        // 1. Find all Scouts
        ScoutBotCore[] scouts = FindObjectsOfType<ScoutBotCore>();
        if (scouts.Length == 0)
        {
            Debug.LogWarning("[MissionController] No ScoutBots found.");
            return;
        }

        // Sort scouts by name to ensure deterministic assignment (Scout 1, Scout 2, etc.)
        System.Array.Sort(scouts, (a, b) => string.Compare(a.name, b.name));

        Debug.Log($"[MissionController] Found {scouts.Length} scouts. Preparing distribution...");

        // 2. Find all Harvest Spots
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
        // 3. Sort spots by X (Vertical Strips strategy)
        // This ensures Scout 1 gets the left strip, Scout 2 the next, etc.
        // We sort by X first, then Y for stability
        harvestSpots.Sort((a, b) => {
            int xComp = a.x.CompareTo(b.x);
            if (xComp != 0) return xComp;
            return a.y.CompareTo(b.y);
        });

        Debug.Log($"[MissionController] Found {harvestSpots.Count} harvest spots. Distributing...");

        // 4. Split and Assign
        int totalSpots = harvestSpots.Count;
        int spotsPerScout = totalSpots / scouts.Length;
        int remainder = totalSpots % scouts.Length;
        int currentIndex = 0;

        for (int i = 0; i < scouts.Length; i++)
        {
            // Distribute remainder one by one to the first few scouts
            int count = spotsPerScout + (i < remainder ? 1 : 0);
            
            if (count > 0)
            {
                List<Vector2Int> assignedSpots = harvestSpots.GetRange(currentIndex, count);
                currentIndex += count;

                // Assign to scout and tell it to optimize the path
                Debug.Log($"[MissionController] Assigning {assignedSpots.Count} spots to {scouts[i].name} (Range X: {assignedSpots[0].x} to {assignedSpots[assignedSpots.Count-1].x})");
                scouts[i].AsignarMision(assignedSpots, optimize: true);
            }
            else
            {
                Debug.LogWarning($"[MissionController] Scout {scouts[i].name} got 0 spots (too many scouts?)");
            }
        }
    }
}
