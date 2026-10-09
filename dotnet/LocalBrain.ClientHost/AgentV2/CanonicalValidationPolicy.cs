using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

internal sealed partial class CanonicalStore
{
    internal sealed record ValidationPublisher(string RegistrationTask,string EventId,string PolicyHash);
    private readonly Dictionary<string,int> validationPublisherLeases=new(StringComparer.Ordinal);
    private sealed class PublisherLease(CanonicalStore owner,string repository) : IDisposable {
        private bool disposed;
        public void Dispose(){lock(owner.gate){if(disposed)return;disposed=true;owner.validationPublisherLeases[repository]--;}}
    }
    internal ValidationPublisher? CurrentValidationPublisher(string repository){
        lock(gate){
            if(Convert.ToInt64(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='validation_publisher_registry';"))==0)return null;
            using var cmd=Command("""
                SELECT r.registration_task,r.event_id,r.policy_hash FROM validation_publisher_registry r
                JOIN validation_publisher_audit a ON a.repository_id=r.repository_id AND a.event_id=r.event_id AND a.new_hash=r.policy_hash
                WHERE r.repository_id=$repo AND a.audit_sequence=(SELECT max(audit_sequence) FROM validation_publisher_audit WHERE repository_id=$repo)
                """,null,("$repo",repository));
            using var reader=cmd.ExecuteReader();return reader.Read()?new(reader.GetString(0),reader.GetString(1),reader.GetString(2)):null;
        }
    }
    internal IDisposable AcquireValidationPublisher(string repository,string hash){
        lock(gate){if(CurrentValidationPublisher(repository)?.PolicyHash!=hash)throw new UnauthorizedAccessException("Validation publisher changed before dispatch");
            validationPublisherLeases[repository]=validationPublisherLeases.GetValueOrDefault(repository)+1;return new PublisherLease(this,repository);}
    }
    internal EventReceipt RegisterValidationPublisher(string task,string workspace,string repository,string hash){
        ValidateId(repository);ValidateHash(hash);
        lock(gate){
            if(validationPublisherLeases.GetValueOrDefault(repository)!=0)throw new IOException("Publisher change waits for active Host validation");
            Execute("""
                CREATE TABLE IF NOT EXISTS validation_publisher_registry(repository_id TEXT PRIMARY KEY,
                registration_task TEXT NOT NULL REFERENCES tasks,event_id TEXT NOT NULL REFERENCES events,
                policy_hash TEXT NOT NULL REFERENCES evidence);
                CREATE TABLE IF NOT EXISTS validation_publisher_audit(audit_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                repository_id TEXT NOT NULL,old_hash TEXT,new_hash TEXT NOT NULL REFERENCES evidence,
                event_id TEXT NOT NULL UNIQUE REFERENCES events);
                CREATE TRIGGER IF NOT EXISTS validation_publisher_audit_no_update BEFORE UPDATE ON validation_publisher_audit BEGIN SELECT RAISE(ABORT,'immutable policy audit'); END;
                CREATE TRIGGER IF NOT EXISTS validation_publisher_audit_no_delete BEFORE DELETE ON validation_publisher_audit BEGIN SELECT RAISE(ABORT,'retained policy audit'); END;
                """);
            var old=CurrentValidationPublisher(repository)?.PolicyHash;var key=Guid.NewGuid().ToString("N");
            var input=new EventInput(task,workspace,"windows_host",key,"validation_publisher_policy_registered","host_policy_audit","host_verified",[hash],JsonSerializer.SerializeToElement(new{repository_id=repository,old_hash=old,new_hash=hash}));
            return CommitEvent(input,null,0,0,0,0,tx=>{
                var id=Convert.ToString(Scalar("SELECT event_id FROM events WHERE task_id=$task AND producer='windows_host' AND idempotency_key=$key",tx,("$task",task),("$key",key)))!;
                Execute("INSERT INTO validation_publisher_audit(repository_id,old_hash,new_hash,event_id) VALUES($repo,$old,$new,$event)",tx,("$repo",repository),("$old",old),("$new",hash),("$event",id));
                Execute("""
                    INSERT INTO validation_publisher_registry(repository_id,registration_task,event_id,policy_hash) VALUES($repo,$task,$event,$hash)
                    ON CONFLICT(repository_id) DO UPDATE SET registration_task=excluded.registration_task,event_id=excluded.event_id,policy_hash=excluded.policy_hash
                    """,tx,("$repo",repository),("$task",task),("$event",id),("$hash",hash));
            });
        }
    }
}
