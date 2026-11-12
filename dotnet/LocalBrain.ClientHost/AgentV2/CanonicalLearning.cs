namespace LocalBrain.ClientHost.AgentV2;

internal sealed partial class CanonicalStore
{
    internal StoredEvent[] RecentPromotedLearning(string taskId,string workspace)
    {
        var rows=new List<(string Task,string Event)>();
        lock(gate)
        {
            using var command=Command("""
                SELECT learning.task_id,learning.event_id FROM events AS learning
                JOIN tasks ON tasks.task_id=learning.task_id
                WHERE learning.task_id!=$task AND tasks.workspace_id=$workspace
                    AND learning.workspace_id=$workspace AND learning.event_type='differential_learning_packet'
                    AND learning.producer='windows_host' AND learning.verification_status='host_verified'
                    AND json_extract(tasks.state_json,'$.phase')='complete'
                    AND EXISTS (
                        SELECT 1 FROM events AS promotion
                        WHERE promotion.task_id=learning.task_id AND promotion.workspace_id=$workspace
                            AND promotion.event_type='codex_rescue_promoted' AND promotion.producer='windows_host'
                            AND promotion.verification_status='host_verified'
                            AND json_extract(promotion.payload_json,'$.input.Metadata.validation_hash')=
                                json_extract(learning.payload_json,'$.input.Metadata.validation_hash')
                    )
                ORDER BY learning.rowid DESC LIMIT 24;
                """,null,("$task",taskId),("$workspace",workspace));
            using var reader=command.ExecuteReader();
            while(reader.Read()) rows.Add((reader.GetString(0),reader.GetString(1)));
        }
        return rows.Select(row=>ReadTaskEvent(row.Task,row.Event)).ToArray();
    }
}
