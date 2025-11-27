using System.Collections.Generic;
using UnityEngine;

public class TomatoFieldManager : MonoBehaviour
{
    public static TomatoFieldManager Instance { get; private set; }

    [System.Serializable]
    public class TomatoTask
    {
        public Vector2Int standPos;  // tile donde se para el bot
        public Vector2Int plantPos;  // tile de la cama con planta
        public int tomatoes;         // tomates restantes (lógicos)

        // Tomates visuales instanciados en la planta
        public List<GameObject> tomatoVisuals = new List<GameObject>();

        // Estado de enfermedad de cada tomate (paralelo a tomatoVisuals)
        public List<bool> diseasedTomatoes = new List<bool>();
    }

    [Header("Tareas de cosecha")]
    public List<TomatoTask> allTasks = new List<TomatoTask>();

    private GridManager grid;
    private bool tasksBuilt = false;

    [Header("Visual de tomates")]
    public GameObject tomatoPrefab;       // tu esfera/bolita de tomate
    public float tomatoHeightStep = 0.15f; // separación en altura entre tomates
    public float tomatoRadiusOffset = 0.15f; // pequeño offset random en X/Z

    [Header("Tomates enfermos")]
    [Range(0f, 1f)]
    public float diseaseProbability = 0.20f; // 20% de probabilidad de enfermedad
    public GameObject sickTomatoPrefab;      // prefab para tomates enfermos

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

    // Garantiza que las tareas se construyan solo una vez
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
            grid = GridManager.Instance;

        // Recorremos todos los tiles del grid
        foreach (var kvp in grid.AllTiles)
        {
            Vector2Int pos = kvp.Key;
            TileInfo tile = kvp.Value;

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
                Debug.LogWarning(
                    $"HarvestSpot en {pos} no encontró planta vecina. Revisa layout."
                );
                continue;
            }

            // Crear tarea con tomates aleatorios
            TomatoTask task = new TomatoTask
            {
                standPos = pos,
                plantPos = plantPos.Value,
                tomatoes = Random.Range(1, 6),
                tomatoVisuals = new List<GameObject>()
            };

            // Instanciar tomates visuales sobre la planta
            SpawnTomatoesForTask(task);

            allTasks.Add(task);
        }

        Debug.Log($"TomatoFieldManager: generadas {allTasks.Count} tareas de cosecha.");
    }

    private void SpawnTomatoesForTask(TomatoTask task)
    {
        if (tomatoPrefab == null)
        {
            Debug.LogWarning("TomatoFieldManager: tomatoPrefab no asignado en el inspector.");
            return;
        }

        // Posición base en mundo de la planta
        Vector3 basePos = grid.GridToWorld(task.plantPos);

        for (int i = 0; i < task.tomatoes; i++)
        {
            // Determinar si este tomate está enfermo
            bool isSick = Random.value < diseaseProbability;
            task.diseasedTomatoes.Add(isSick);

            // Pequeño random en X/Z para que no queden perfectamente alineados
            float offsetX = Random.Range(-tomatoRadiusOffset, tomatoRadiusOffset);
            float offsetZ = Random.Range(-tomatoRadiusOffset, tomatoRadiusOffset);
            float offsetY = (i + 0.5f) * tomatoHeightStep;

            Vector3 spawnPos = basePos + new Vector3(offsetX, offsetY, offsetZ);

            // Seleccionar prefab según estado de salud
            GameObject prefabToUse = isSick && sickTomatoPrefab != null ? sickTomatoPrefab : tomatoPrefab;
            GameObject go = Instantiate(prefabToUse, spawnPos, Quaternion.identity, transform);

            task.tomatoVisuals.Add(go);
        }
    }

    /// <summary>
    /// Consume visualmente 1 tomate de la tarea dada.
    /// También decrementa el contador lógico de tomates.
    /// </summary>
    /// <summary>
    /// Consume visualmente 1 tomate de la tarea dada.
    /// También decrementa el contador lógico de tomates.
    /// Retorna true si el tomate consumido estaba enfermo.
    /// </summary>
    public bool ConsumeTomato(TomatoTask task)
    {
        if (task == null) return false;
        if (task.tomatoes <= 0) return false;

        task.tomatoes--;

        bool wasSick = false;

        // Eliminar estado de enfermedad del último tomate
        if (task.diseasedTomatoes != null && task.diseasedTomatoes.Count > 0)
        {
            int idx = task.diseasedTomatoes.Count - 1;
            wasSick = task.diseasedTomatoes[idx];
            task.diseasedTomatoes.RemoveAt(idx);
        }

        // Eliminar el visual del último tomate
        if (task.tomatoVisuals != null && task.tomatoVisuals.Count > 0)
        {
            int idx = task.tomatoVisuals.Count - 1;
            GameObject go = task.tomatoVisuals[idx];
            task.tomatoVisuals.RemoveAt(idx);

            if (go != null)
                Destroy(go);
        }

        return wasSick;
    }
}
