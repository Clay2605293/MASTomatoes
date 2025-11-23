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

    // Totales acumulados en tiempo de ejecución (actualizados por los bots)
    [Header("Totals (acumulados en runtime)")]
    public int totalTilesMoved = 0;
    public int totalReplans = 0;
    public int totalTomatoesPicked = 0;
    public int totalTomatoesDelivered = 0;
    public int totalTasksCompleted = 0;

    public void RegisterTileMoved() { totalTilesMoved++; }
    public void RegisterReplan() { totalReplans++; }
    public void RegisterTomatoPicked() { totalTomatoesPicked++; }
    public void RegisterTomatoDelivered() { totalTomatoesDelivered++; }
    public void RegisterTaskCompleted() { totalTasksCompleted++; }

    // ----------------- Registro por bot (para reporte final) -----------------
    private class BotStatsEntry
    {
        public string name;
        public int tilesMoved;
        public int replans;
        public int tomatoesPicked;
        public int tomatoesDelivered;
        public int tasksCompleted;
    }

    private List<BotStatsEntry> botStatsEntries = new();

    public void RegisterBotFinalStats(BotController bot)
    {
        if (bot == null) return;

        var entry = new BotStatsEntry()
        {
            name = bot.name,
            tilesMoved = bot.tilesMoved,
            replans = bot.totalReplans,
            tomatoesPicked = bot.tomatoesPicked,
            tomatoesDelivered = bot.tomatoesDelivered,
            tasksCompleted = bot.completedTasks
        };

        botStatsEntries.Add(entry);

        // Log individual
        Debug.Log($"[Stats][Bot] {entry.name} -> tiles:{entry.tilesMoved} | replans:{entry.replans} | picked:{entry.tomatoesPicked} | delivered:{entry.tomatoesDelivered} | tasks:{entry.tasksCompleted}");

        // Si ya recibimos todos los bots esperados, imprimimos resumen final
        if (botStatsEntries.Count == expectedBotsForStats)
        {
            PrintFinalAggregatedStats();
        }
    }

    private void PrintFinalAggregatedStats()
    {
        Debug.Log($"------------------------------");
        Debug.Log($"[Stats] RESULTADOS FINALES (por bot)");
        Debug.Log($"------------------------------");

        // Detalle por bot
        foreach (var e in botStatsEntries)
        {
            Debug.Log($"[Stats][Bot] {e.name}: tiles={e.tilesMoved}, replans={e.replans}, picked={e.tomatoesPicked}, delivered={e.tomatoesDelivered}, tasks={e.tasksCompleted}");
        }

        // Totales (ya acumulados en tiempo de ejecución)
        Debug.Log($"------------------------------");
        Debug.Log($"[Stats] ----- MÉTRICAS GLOBALES -----");
        Debug.Log($"[Stats] Tiles recorridos totales: {totalTilesMoved}");
        Debug.Log($"[Stats] Replanificaciones totales: {totalReplans}");
        Debug.Log($"[Stats] Tomates recolectados: {totalTomatoesPicked}");
        Debug.Log($"[Stats] Tomates entregados: {totalTomatoesDelivered}");
        Debug.Log($"[Stats] Tareas completadas totales: {totalTasksCompleted}");

        // Promedios por bot (usar expectedBotsForStats para división segura)
        float denom = Mathf.Max(1, expectedBotsForStats);
        Debug.Log($"[Stats] ----- PROMEDIOS (por bot) -----");
        Debug.Log($"[Stats] Promedio de tiles recorridos: {((float)totalTilesMoved) / denom:F2}");
        Debug.Log($"[Stats] Promedio de replanificaciones: {((float)totalReplans) / denom:F2}");
        Debug.Log($"[Stats] Promedio de tomates recolectados: {((float)totalTomatoesPicked) / denom:F2}");
        Debug.Log($"[Stats] Promedio de tomates entregados: {((float)totalTomatoesDelivered) / denom:F2}");
        Debug.Log($"[Stats] Promedio de tareas completadas: {((float)totalTasksCompleted) / denom:F2}");
    }

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
            // Nota: El resumen global más detallado se imprime cuando se reciben
            // los registros finales de cada bot (RegisterBotFinalStats).
        }
    }
}
