using System.Collections.Generic;
using UnityEngine;

public class TomatoFieldManager : MonoBehaviour
{
    public static TomatoFieldManager Instance { get; private set; }

    // Tarea lógica de cosecha
    public class TomatoTask
    {
        public Vector2Int standPos;  // tile donde se para el bot
        public Vector2Int plantPos;  // tile de la cama con planta
        public int tomatoes;         // 1–5 tomates
        public bool completed = false;
    }

    public List<TomatoTask> allTasks = new List<TomatoTask>();

    [Header("Visual de tomates")]
    public GameObject tomatoPrefab;       // bolita de tomate
    public float tomatoHeightStep = 0.2f; // separación en altura (Y) entre bolitas

    private GridManager grid;
    private bool tasksBuilt = false;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        grid = GridManager.Instance;
        EnsureTasksBuilt();
    }

    // Garantiza que las tareas estén listas (por si alguien las pide en Start)
    public void EnsureTasksBuilt()
    {
        if (tasksBuilt) return;
        BuildTasksFromTiles();
        tasksBuilt = true;
    }

    private void BuildTasksFromTiles()
    {
        allTasks.Clear();

        if (grid == null)
        {
            grid = GridManager.Instance;
        }

        // (opcional) limpiar tomates viejos si vuelves a construir
        if (tomatoPrefab != null)
        {
            foreach (Transform child in transform)
            {
                Destroy(child.gameObject);
            }
        }

        // Recorremos todos los tiles del grid
        foreach (var kvp in grid.AllTiles)
        {
            Vector2Int pos = kvp.Key;
            TileInfo tile = kvp.Value;

            // Solo nos interesan los tiles donde se para el bot para cosechar
            if (!tile.isHarvestSpot)
                continue;

            // Buscar planta vecina en 4 direcciones
            Vector2Int[] dirs =
            {
                Vector2Int.up,
                Vector2Int.down,
                Vector2Int.left,
                Vector2Int.right
            };

            Vector2Int? plantPos = null;

            foreach (var d in dirs)
            {
                Vector2Int neighbor = pos + d;
                TileInfo neighborTile = grid.GetTile(neighbor);
                if (neighborTile == null) continue;

                if (!neighborTile.walkable && neighborTile.hasPlant)
                {
                    plantPos = neighbor;
                    break;
                }
            }

            if (plantPos == null)
            {
                Debug.LogWarning($"HarvestSpot en {pos} no encontró planta vecina. Revisa layout.");
                continue;
            }

            // Cuántos tomates tiene esta planta
            int tomatoCount = Random.Range(1, 6); // 1–5

            TomatoTask task = new TomatoTask
            {
                standPos = pos,
                plantPos = plantPos.Value,
                tomatoes = tomatoCount
            };

            allTasks.Add(task);

            // --------- Visual: instanciar las bolitas de tomate ----------
            if (tomatoPrefab != null)
            {
                // Centro del tile de planta
                Vector3 basePos = grid.GridToWorld(plantPos.Value);

                for (int i = 0; i < tomatoCount; i++)
                {
                    float yOffset = tomatoHeightStep * (i + 1);
                    Vector3 tomatoPos = basePos + new Vector3(0f, yOffset, 0f);

                    Instantiate(
                        tomatoPrefab,
                        tomatoPos,
                        Quaternion.identity,
                        this.transform   // las cuelgo del TomatoFieldManager
                    );
                }
            }
        }

        Debug.Log($"TomatoFieldManager: generadas {allTasks.Count} tareas de cosecha.");
    }
}
