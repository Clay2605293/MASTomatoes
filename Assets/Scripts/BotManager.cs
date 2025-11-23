using System.Collections.Generic;
using UnityEngine;

public class BotManager : MonoBehaviour
{
    public static BotManager Instance { get; private set; }

    // ----------------- OCUPACIÓN DE CELDAS -----------------

    private Dictionary<Vector2Int, BotController> occupiedCells = new();
    private System.Random rng = new System.Random();

    // ----------------- ESTADÍSTICAS GLOBALES DE BOTS -----------------
    [Header("Stats de bots")]
    [Tooltip("Cuántos bots esperamos que terminen para calcular el promedio")]
    public int expectedBotsForStats = 5;

    private int botsFinishedForStats = 0;
    private float sumTimesForStats = 0f;
    private float minTimeForStats = float.MaxValue;
    private float maxTimeForStats = 0f;

    // ----------------- DOCKING STATION (DS) -----------------

    [Header("Docking Station Queue")]
    public List<Transform> dockingQueueSlots;   // puntos físicos de fila DS, en orden 1,2,3,...

    private Vector2Int[] dockingQueueGridPos;
    private bool dockingQueueBuilt = false;

    private BotController dockingOwner;
    private Queue<BotController> dockingQueueOrder = new();               // orden FIFO
    private Dictionary<BotController, int> dockingQueueIndex = new();     // bot -> slot index

    // ----------------- ENTREGA DE COSECHA (EC) -----------------

    [Header("Entrega de Cosecha (EC) Queue")]
    public List<Transform> ecQueueSlots;        // puntos físicos de fila EC, en orden 1,2,3,...

    private Vector2Int[] ecQueueGridPos;
    private bool ecQueueBuilt = false;

    private BotController ecOwner;
    private Queue<BotController> ecQueueOrder = new();
    private Dictionary<BotController, int> ecQueueIndex = new();

    // ----------------- UNITY -----------------

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        // NO calculamos las filas aquí para no depender del orden de Awake.
    }

    // ----------------- HELPERS: CONSTRUIR POSICIONES DE FILA -----------------

    private void EnsureDockingQueueGridPos()
    {
        if (dockingQueueBuilt) return;

        var grid = GridManager.Instance;
        if (grid == null) return; // se intentará de nuevo más tarde

        if (dockingQueueSlots != null && dockingQueueSlots.Count > 0)
        {
            dockingQueueGridPos = new Vector2Int[dockingQueueSlots.Count];
            for (int i = 0; i < dockingQueueSlots.Count; i++)
            {
                dockingQueueGridPos[i] = grid.WorldToGrid(dockingQueueSlots[i].position);
            }
        }
        else
        {
            dockingQueueGridPos = new Vector2Int[0];
        }

        dockingQueueBuilt = true;
    }

    private void EnsureECQueueGridPos()
    {
        if (ecQueueBuilt) return;

        var grid = GridManager.Instance;
        if (grid == null) return; // se intentará de nuevo más tarde

        if (ecQueueSlots != null && ecQueueSlots.Count > 0)
        {
            ecQueueGridPos = new Vector2Int[ecQueueSlots.Count];
            for (int i = 0; i < ecQueueSlots.Count; i++)
            {
                ecQueueGridPos[i] = grid.WorldToGrid(ecQueueSlots[i].position);
            }
        }
        else
        {
            ecQueueGridPos = new Vector2Int[0];
        }

        ecQueueBuilt = true;
    }

    // ----------------- OCUPACIÓN DE CELDAS -----------------

    public void RegisterBot(BotController bot, Vector2Int gridPos)
    {
        occupiedCells[gridPos] = bot;
    }

    public BotController GetBotAt(Vector2Int gridPos)
    {
        occupiedCells.TryGetValue(gridPos, out var bot);
        return bot;
    }

    public bool TryMoveWithPriority(BotController bot, Vector2Int from, Vector2Int to)
    {
        if (from == to) return true;

        if (!occupiedCells.TryGetValue(to, out BotController other) || other == null)
        {
            if (occupiedCells.TryGetValue(from, out var current) && current == bot)
            {
                occupiedCells.Remove(from);
            }
            occupiedCells[to] = bot;
            return true;
        }

        if (other == bot) return true;

        float myCost = bot.RemainingCost;
        float otherCost = other.RemainingCost;

        bool iHaveHigherPriority = false;
        const float epsilon = 0.01f;

        if (myCost + epsilon < otherCost)
        {
            iHaveHigherPriority = true;
        }
        else if (Mathf.Abs(myCost - otherCost) <= epsilon)
        {
            iHaveHigherPriority = rng.NextDouble() < 0.5;
        }

        if (iHaveHigherPriority)
            other.ForceReplan();
        else
            bot.ForceReplan();

        return false;
    }

    // ----------------- DOCKING STATION (DS) -----------------

    public bool TryClaimDocking(BotController bot)
    {
        // DS ocupado por otro
        if (dockingOwner != null && dockingOwner != bot)
            return false;

        // DS libre
        if (dockingOwner == null)
        {
            // Si hay fila, solo el de hasta adelante puede tomarlo
            if (dockingQueueOrder.Count > 0)
            {
                if (dockingQueueOrder.Peek() != bot)
                    return false;

                dockingQueueOrder.Dequeue();
                dockingQueueIndex.Remove(bot);
            }

            dockingOwner = bot;
            return true;
        }

        // dockingOwner == bot
        return true;
    }

    public void ReleaseDocking(BotController bot)
    {
        if (dockingOwner == bot)
        {
            dockingOwner = null;
        }
    }

    public bool TryGetDockingQueueSlot(BotController bot, out Vector2Int queuePos)
    {
        EnsureDockingQueueGridPos();

        if (dockingQueueGridPos == null || dockingQueueGridPos.Length == 0)
        {
            queuePos = default;
            return false;
        }

        // Ya tiene slot
        if (dockingQueueIndex.TryGetValue(bot, out int idxExisting))
        {
            queuePos = dockingQueueGridPos[idxExisting];
            return true;
        }

        // Buscar primer slot libre
        for (int i = 0; i < dockingQueueGridPos.Length; i++)
        {
            bool used = false;
            foreach (var kv in dockingQueueIndex)
            {
                if (kv.Value == i)
                {
                    used = true;
                    break;
                }
            }

            if (!used)
            {
                dockingQueueIndex[bot] = i;
                dockingQueueOrder.Enqueue(bot);
                queuePos = dockingQueueGridPos[i];
                return true;
            }
        }

        queuePos = default;
        return false;
    }

    public void ReleaseDockingQueueSlot(BotController bot)
    {
        if (!dockingQueueIndex.Remove(bot))
            return;

        if (dockingQueueOrder.Count > 0)
        {
            var tmp = new Queue<BotController>();
            while (dockingQueueOrder.Count > 0)
            {
                var b = dockingQueueOrder.Dequeue();
                if (b != bot) tmp.Enqueue(b);
            }
            dockingQueueOrder = tmp;
        }
    }

    // ----------------- EC (ENTREGA DE COSECHA) -----------------

    public bool TryClaimEC(BotController bot)
    {
        if (ecOwner != null && ecOwner != bot)
            return false;

        if (ecOwner == null)
        {
            // Si hay fila, solo el primero puede entrar
            if (ecQueueOrder.Count > 0)
            {
                if (ecQueueOrder.Peek() != bot)
                    return false;

                ecQueueOrder.Dequeue();
                ecQueueIndex.Remove(bot);
            }

            ecOwner = bot;
            return true;
        }

        // ecOwner == bot
        return true;
    }

    public void ReleaseEC(BotController bot)
    {
        if (ecOwner == bot)
        {
            ecOwner = null;
        }
    }

    public bool TryGetECQueueSlot(BotController bot, out Vector2Int queuePos)
    {
        EnsureECQueueGridPos();

        if (ecQueueGridPos == null || ecQueueGridPos.Length == 0)
        {
            queuePos = default;
            return false;
        }

        if (ecQueueIndex.TryGetValue(bot, out int idxExisting))
        {
            queuePos = ecQueueGridPos[idxExisting];
            return true;
        }

        for (int i = 0; i < ecQueueGridPos.Length; i++)
        {
            bool used = false;
            foreach (var kv in ecQueueIndex)
            {
                if (kv.Value == i)
                {
                    used = true;
                    break;
                }
            }

            if (!used)
            {
                ecQueueIndex[bot] = i;
                ecQueueOrder.Enqueue(bot);
                queuePos = ecQueueGridPos[i];
                return true;
            }
        }

        queuePos = default;
        return false;
    }

    public void ReleaseECQueueSlot(BotController bot)
    {
        if (!ecQueueIndex.Remove(bot))
            return;

        if (ecQueueOrder.Count > 0)
        {
            var tmp = new Queue<BotController>();
            while (ecQueueOrder.Count > 0)
            {
                var b = ecQueueOrder.Dequeue();
                if (b != bot) tmp.Enqueue(b);
            }
            ecQueueOrder = tmp;
        }
    }

    // ----------------- MÉTODO PARA REPORTAR TIEMPOS -----------------
    public void ReportBotFinished(float timeSeconds)
    {
        botsFinishedForStats++;
        sumTimesForStats += timeSeconds;

        if (timeSeconds < minTimeForStats) minTimeForStats = timeSeconds;
        if (timeSeconds > maxTimeForStats) maxTimeForStats = timeSeconds;

        Debug.Log($"[Stats] Bot terminado #{botsFinishedForStats} con tiempo = {timeSeconds:F2} s");

        if (botsFinishedForStats == expectedBotsForStats)
        {
            float average = sumTimesForStats / expectedBotsForStats;
            Debug.Log($"[Stats] Tiempo promedio de {expectedBotsForStats} bots para recolectar los tomates objetivo: {average:F2} segundos.");
            Debug.Log($"[Stats] Min: {minTimeForStats:F2} s, Max: {maxTimeForStats:F2} s");
        }
    }
}