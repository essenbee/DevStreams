﻿using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using DevChatter.DevStreams.Core.Data;
using DevChatter.DevStreams.Core.Model;
using DevChatter.DevStreams.Core.Services;
using DevChatter.DevStreams.Core.Settings;
using DevChatter.DevStreams.Web.Alexa;
using Essenbee.Alexa.Lib;
using Essenbee.Alexa.Lib.Interfaces;
using Essenbee.Alexa.Lib.Request;
using Essenbee.Alexa.Lib.Response;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevChatter.DevStreams.Web.Controllers
{
    [Produces("application/json")]
    [ApiController]
    public class AlexaController : ControllerBase
    {
        private IConfiguration _config;
        private ILogger<AlexaController> _logger;
        private readonly IAlexaClient _client;
        private string _userTimeZone = string.Empty;
        private ICrudRepository _repo;
        private ITwitchService _twitchService;
        private readonly DatabaseSettings _dbSettings;

        public AlexaController(IConfiguration config, ILogger<AlexaController> logger, IAlexaClient client,
            ICrudRepository crudRepository, ITwitchService twitchService, IOptions<DatabaseSettings> dbSettings)
        {
            _config = config;
            _logger = logger;
            _client = client;
            _repo = crudRepository;
            _twitchService = twitchService;
            _dbSettings = dbSettings.Value;
        }

        [HttpPost]
        [ProducesResponseType(200, Type = typeof(AlexaResponse))]
        [ProducesResponseType(400)]
        [Route("api/alexa/devstreams")]
        public async Task<ActionResult<AlexaResponse>> DevStreams([FromBody] AlexaRequest alexaRequest)
        {
            if (!AlexaRequest.ShouldProcessRequest(_config["SkillId"], alexaRequest))
            {
                _logger.LogError("Bad Request - application id did not match or timestamp tolerance exceeded!");

                return BadRequest();
            }

            AlexaResponse response = null;
            var responseBuilder = new ResponseBuilder();

            _userTimeZone = await _client.GetUserTimezone(alexaRequest, _logger);
            _userTimeZone = _userTimeZone.Replace("\"", string.Empty);

            switch (alexaRequest.RequestBody.Type)
            {
                case "LaunchRequest":
                    var ssml = @"<speak>Welcome to the Dev Streams skill</speak>";
                    response = responseBuilder.SayWithSsml(ssml)
                        .Build();
                    break;
                case "IntentRequest":
                    response = await IntentRequestHandler(alexaRequest);
                    break;
                case "SessionEndedRequest":
                    response = SessionEndedRequestHandler(alexaRequest);
                    break;
                default:
                    break;
            }

            return response;
        }

        private AlexaResponse SessionEndedRequestHandler(AlexaRequest alexaRequest)
        {
            var sessionEndedRequest = alexaRequest.RequestBody as SessionEndedRequest;
            if (sessionEndedRequest.Error != null)
            {
                var error = sessionEndedRequest.Error;
                _logger.LogError($"{error.ErrorType} - {error.ErrorMessage}");
            }

            return null;
        }

        private async Task<AlexaResponse> IntentRequestHandler(AlexaRequest alexaRequest)
        {
            AlexaResponse response = null;

            if (alexaRequest.RequestBody is IntentRequest intentRequest)
            {
                switch (intentRequest.Intent.Name)
                {
                    case "whenNextIntent":
                        response = await WhenNextResponseHandler(intentRequest);
                        break;
                    case "whoIsLiveIntent":
                        response = await WhoIsLiveResponseHandler(intentRequest);
                        break;
                    case "AMAZON.StopIntent":
                    case "AMAZON.CancelIntent":
                        response = CancelOrStopResponseHandler(intentRequest);
                        break;
                    case "AMAZON.HelpIntent":
                        response = HelpIntentResponseHandler(intentRequest);
                        break;
                }
            }

            return response;
        }

        private AlexaResponse HelpIntentResponseHandler(IntentRequest intentRequest)
        {
            var response = new ResponseBuilder()
                .Say("To use this skill, ask me about when your favourite channel is streaming next; or who is broadcasting now. " +
                "You can also say Alexa Stop to exit the skill")
                .Build(); ;
            return response;
        }

        private AlexaResponse CancelOrStopResponseHandler(IntentRequest intentRequest)
        {
            var response = new ResponseBuilder()
                .Say("Thanks for using the Dev Streams skill")
                .Build();
            return response;
        }

        private async Task<AlexaResponse> WhenNextResponseHandler(IntentRequest intentRequest)
        {
            if (FoundChannelSlot(intentRequest))
            {
                var channel = intentRequest.Intent.Slots["channel"].Value;

                _logger.LogInformation($"User asked for: {channel}");

                var standardisedChannel = channel
                    .Replace(" ", string.Empty)
                    .Replace(".", string.Empty);

                var dbChannel = new Channel();

                // ToDo: Extract this
                var sql = $"SELECT * FROM Channels WHERE SOUNDEX(Name) = SOUNDEX(@standardisedChannel) ORDER BY DIFFERENCE(Name, @standardisedChannel) DESC";
                using (System.Data.IDbConnection connection = new SqlConnection(_dbSettings.DefaultConnection))
                {
                    using (var multi = await connection.QueryMultipleAsync(sql, new { standardisedChannel }))
                    {
                        dbChannel = (await multi.ReadAsync<Channel>()).FirstOrDefault();
                    }
                }

                var response = await Responses.GetNextStreamResponse(_userTimeZone, channel, dbChannel, _dbSettings);
                return response;
            }
            else
            {
                // ToDo: Initiate dialog with use
                return new ResponseBuilder()
                    .Say("I am sorry but I did not catch which streamer you are enquiring about")
                    .Build();
            }
        }

        private async Task<AlexaResponse> WhoIsLiveResponseHandler(IntentRequest intentRequest)
        {
            List<Channel> channels = await _repo.GetAll<Channel>();
            List<string> channelNames = channels.Select(x => x.Name).ToList();
            var liveChannels = await _twitchService.GetLiveChannels(channelNames);
            var response = Responses.GetLiveNowResponse(liveChannels);

            return response;
        }

        private bool FoundChannelSlot(IntentRequest intentRequest) => intentRequest.Intent.Slots.Any() && intentRequest?.Intent?.Slots["channel"]?.Value != null;
    }
}