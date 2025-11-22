using UnityEngine;

public class GridObstacle : MonoBehaviour
{
    public bool isPermanent = false; // true = cambia el tile a no walkable
    public bool isMoving = false;    // true = el objeto se puede mover por el grid

    private GridManager grid;
    private Vector2Int currentPos;
    private bool registered = false;

    private void Start()
    {
        grid = GridManager.Instance;
        UpdateGridPos(initial: true);
    }

    private void Update()
    {
        if (!isMoving || grid == null) return;

        UpdateGridPos(initial: false);
    }

    private void OnDestroy()
    {
        if (!registered || grid == null) return;

        // Si era dinámico, libera la última casilla
        if (!isPermanent)
        {
            grid.SetDynamicBlocked(currentPos, false);
        }
        // Si era permanente podrías dejarlo tal cual (el piso queda no walkable)
        // o revertirlo si quieres:
        // else grid.SetTileWalkable(currentPos, true);
    }

    private void UpdateGridPos(bool initial)
    {
        Vector2Int newPos = grid.WorldToGrid(transform.position);

        if (initial)
        {
            currentPos = newPos;
            if (isPermanent)
            {
                grid.SetTileWalkable(currentPos, false);
            }
            else
            {
                grid.SetDynamicBlocked(currentPos, true);
            }
            registered = true;
        }
        else
        {
            if (newPos == currentPos) return;

            // Se movió de tile: liberar el anterior y bloquear el nuevo
            if (isPermanent)
            {
                // permanente normalmente no debería moverse, pero por si acaso:
                grid.SetTileWalkable(currentPos, true);
                grid.SetTileWalkable(newPos, false);
            }
            else
            {
                grid.SetDynamicBlocked(currentPos, false);
                grid.SetDynamicBlocked(newPos, true);
            }

            currentPos = newPos;
        }
    }
}
