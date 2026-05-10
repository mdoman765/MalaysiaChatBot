using System.Text.Json;

namespace crud_app_backend.Bot.Services
{
    public interface IUaeBotService
    {
        Task ProcessAsync(JsonElement body);
    }
}
