using System.Collections.Generic;
using UnityEngine;

public class TaskDistributor : MonoBehaviour
{
    [Header("Bots que participarán")]
    public List<BotController> bots = new List<BotController>();

    private void Start()
    {
        var field = TomatoFieldManager.Instance;
        if (field == null)
        {
            return;
        }

        // Asegurarnos de que las tareas estén creadas
        field.EnsureTasksBuilt();

        var tasks = new List<TomatoFieldManager.TomatoTask>(field.allTasks);

        if (bots.Count == 0)
        {
            return;
        }

        if (tasks.Count == 0)
        {
            return;
        }

        // 1) Ordenamos las tareas por posición (fila, luego columna)
        tasks.Sort((a, b) =>
        {
            int cmp = a.standPos.x.CompareTo(b.standPos.x);
            if (cmp != 0) return cmp;
            return a.standPos.y.CompareTo(b.standPos.y);
        });

        int P = tasks.Count;
        int B = bots.Count;

        int baseCount = P / B;
        int extra = P % B;
        int index = 0;

        for (int i = 0; i < B; i++)
        {
            int count = baseCount + (i < extra ? 1 : 0);

            if (index + count > P)
            {
                count = Mathf.Max(0, P - index);
            }

            var sublist = tasks.GetRange(index, count);

            if (sublist.Count > 0)
            {
                bots[i].SetAssignedTasks(sublist);
            }

            index += count;
        }
    }
}
