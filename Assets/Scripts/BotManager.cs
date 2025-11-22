using System.Collections.Generic;
using UnityEngine;

public class BotManager : MonoBehaviour
{
    public static BotManager Instance { get; private set; }

    // Qué bot está en qué celda
    private Dictionary<Vector2Int, BotController> occupiedCells = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public void RegisterBot(BotController bot, Vector2Int gridPos)
    {
        occupiedCells[gridPos] = bot;
    }

    public void UpdateBotPosition(BotController bot, Vector2Int oldPos, Vector2Int newPos)
    {
        if (occupiedCells.ContainsKey(oldPos) && occupiedCells[oldPos] == bot)
        {
            occupiedCells.Remove(oldPos);
        }
        occupiedCells[newPos] = bot;
    }

    public bool IsCellFree(Vector2Int gridPos, BotController requester)
    {
        if (!occupiedCells.TryGetValue(gridPos, out var other)) return true;
        // si la ocupa el mismo bot, está bien
        return other == requester;
    }
}
