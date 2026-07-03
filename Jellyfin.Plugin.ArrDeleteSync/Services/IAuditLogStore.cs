using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface IAuditLogStore
{
    Task AppendAsync(AuditLogEntry entry);
    Task<IReadOnlyList<AuditLogEntry>> GetAllAsync();
}
