using System.Collections.Generic;
using UnityEngine;

public class GridManager : MonoBehaviour
{
    public static GridManager Instance { get; private set; }

    [Header("Config del grid")]
    public float cellSize = 1f;   // tamaño entre centros de tiles en X/Z

    // Mapa lógico: de coordenadas de grilla -> TileInfo
    private Dictionary<Vector2Int, TileInfo> tiles;

    // Obstáculos dinámicos (humanos, filas bloqueadas temporalmente, etc.)
    private HashSet<Vector2Int> dynamicBlocked = new HashSet<Vector2Int>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        BuildGrid();
    }

    private void BuildGrid()
    {
        tiles = new Dictionary<Vector2Int, TileInfo>();

        TileInfo[] allTiles = GetComponentsInChildren<TileInfo>();

        foreach (TileInfo tile in allTiles)
        {
            Vector3 pos = tile.transform.position;

            // Ajuste para tiles centrados en .5 (por ejemplo -8.5, -7.5, etc.)
            int gx = Mathf.FloorToInt(pos.x / cellSize + 0.5f);
            int gz = Mathf.FloorToInt(pos.z / cellSize + 0.5f);
            Vector2Int gridPos = new Vector2Int(gx, gz);

            tile.gridPos = gridPos;

            if (!tiles.ContainsKey(gridPos))
            {
                tiles.Add(gridPos, tile);
            }
            else
            {
                // Por si hay dos tiles en el mismo lugar (no debería)
                Debug.LogWarning($"Tile duplicado en {gridPos}");
            }
        }

        Debug.Log($"Grid construido con {tiles.Count} tiles.");
    }

    // ---- helpers de walkability ----

    // Solo revisa el tile estático (piso vs cama vs fuera)
    private bool IsWalkableStatic(Vector2Int gridPos)
    {
        if (tiles.TryGetValue(gridPos, out TileInfo tile))
        {
            return tile.walkable;
        }
        return false;
    }

    // Versión simple: solo estáticos + bloqueos dinámicos (NO bots)
    public bool IsWalkable(Vector2Int gridPos)
    {
        if (!IsWalkableStatic(gridPos)) return false;
        if (dynamicBlocked.Contains(gridPos)) return false;
        return true;
    }

    // Bloqueos dinámicos (para humanos / filas cerradas)
    public void SetDynamicBlocked(Vector2Int gridPos, bool blocked)
    {
        if (blocked) dynamicBlocked.Add(gridPos);
        else dynamicBlocked.Remove(gridPos);
    }

    public void SetTileWalkable(Vector2Int gridPos, bool walkable)
    {
        if (tiles.TryGetValue(gridPos, out TileInfo tile))
        {
            tile.walkable = walkable;
        }
    }


    public bool IsDynamicallyBlocked(Vector2Int gridPos)
    {
        return dynamicBlocked.Contains(gridPos);
    }

    // ---- conversión mundo <-> grilla ----

    public Vector3 GridToWorld(Vector2Int gridPos)
    {
        // Centro aproximado del tile: índice - 0.5
        float x = (gridPos.x - 0.5f) * cellSize;
        float z = (gridPos.y - 0.5f) * cellSize;
        return new Vector3(x, 0f, z);
    }

    public Vector2Int WorldToGrid(Vector3 worldPos)
    {
        int gx = Mathf.FloorToInt(worldPos.x / cellSize + 0.5f);
        int gz = Mathf.FloorToInt(worldPos.z / cellSize + 0.5f);
        return new Vector2Int(gx, gz);
    }

    // ---- Pathfinding BFS en 4 direcciones, considerando:
    // - tiles estáticos
    // - bloqueos dinámicos
    // - otros bots como obstáculos (excepto en la casilla goal) ----
    public List<Vector2Int> FindPath(Vector2Int start, Vector2Int goal, BotController requester = null)
    {
        Queue<Vector2Int> frontier = new Queue<Vector2Int>();
        frontier.Enqueue(start);

        Dictionary<Vector2Int, Vector2Int> cameFrom = new Dictionary<Vector2Int, Vector2Int>();
        cameFrom[start] = start;

        Vector2Int[] dirs = new Vector2Int[]
        {
            Vector2Int.up,
            Vector2Int.down,
            Vector2Int.left,
            Vector2Int.right
        };

        while (frontier.Count > 0)
        {
            Vector2Int current = frontier.Dequeue();

            if (current == goal)
                break;

            foreach (Vector2Int dir in dirs)
            {
                Vector2Int next = current + dir;

                // fuera del grid
                if (!tiles.ContainsKey(next)) continue;

                // camas / paredes
                if (!IsWalkableStatic(next)) continue;

                // bloqueos dinámicos (humanos, filas cerradas)
                if (dynamicBlocked.Contains(next)) continue;

                // otros bots como obstáculos dinámicos
                if (requester != null && BotManager.Instance != null)
                {
                    BotController other = BotManager.Instance.GetBotAt(next);
                    // si hay otro bot en esa celda y NO es la meta, evitamos pasar por ahí
                    if (other != null && other != requester && next != goal)
                        continue;
                }

                if (cameFrom.ContainsKey(next)) continue; // ya visitado

                frontier.Enqueue(next);
                cameFrom[next] = current;
            }
        }

        // Reconstruir el camino
        List<Vector2Int> path = new List<Vector2Int>();

        if (!cameFrom.ContainsKey(goal))
        {
            // No hay camino
            return path;
        }

        Vector2Int currentPos = goal;
        while (currentPos != start)
        {
            path.Add(currentPos);
            currentPos = cameFrom[currentPos];
        }
        path.Reverse();
        return path;
    }
}
