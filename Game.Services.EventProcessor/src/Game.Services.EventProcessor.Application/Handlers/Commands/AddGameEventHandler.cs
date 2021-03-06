using System.Threading.Tasks;
using Common.Handlers;
using Common.RabbitMq;
using Game.Services.EventProcessor.Core.Entities;
using Game.Services.EventProcessor.Core.Messages.Commands;
using Game.Services.EventProcessor.Core.Messages.Events;
using Game.Services.EventProcessor.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace Game.Services.EventProcessor.Application.Handlers.Commands
{
    public class AddGameEventHandler : ICommandHandler<AddGameEventSource>
    {
        private readonly IGameEventSourceRepository _gameSourceRepository;
        private readonly IBusPublisher _busPublisher;
        private readonly ILogger<AddGameEventSource> _logger;

        public AddGameEventHandler(IGameEventSourceRepository gameSourceRepository,
            IBusPublisher busPublisher, ILogger<AddGameEventSource> logger)
        {
            _gameSourceRepository = gameSourceRepository;
            _busPublisher = busPublisher;
            _logger = logger;
        }

        public async Task HandleAsync(AddGameEventSource command, ICorrelationContext context)
        {
            var gameSource = new GameEventSource(command.Id, command.IsWin, command.Score);
            await _gameSourceRepository.AddAsync(gameSource);

            await _busPublisher.PublishAsync(new GameEventSourceAdded(command.Id, command.Score, command.IsWin), context);
        }
    }
}