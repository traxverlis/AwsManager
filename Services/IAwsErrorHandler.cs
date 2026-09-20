using System;
using System.Threading.Tasks;

namespace AwsManager.Services
{
    public interface IAwsErrorHandler
    {
        Task<bool> HandleExceptionAsync(Exception ex, string context);
    }
}