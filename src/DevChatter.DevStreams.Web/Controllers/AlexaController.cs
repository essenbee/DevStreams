using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using DevChatter.DevStreams.Core.Data;
using DevChatter.DevStreams.Core.Model;
using DevChatter.DevStreams.Core.Services;
using DevChatter.DevStreams.Core.Settings;
using Essenbee.Alexa.Lib;
using Essenbee.Alexa.Lib.Interfaces;
using Essenbee.Alexa.Lib.Request;
using Essenbee.Alexa.Lib.Response;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NodaTime;
using NodaTime.Extensions;

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
            var intentRequest = alexaRequest.RequestBody as IntentRequest;

            AlexaResponse response = null;

            if (intentRequest != null)
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
                .Say("To use this skill, ask me about the schedule of your favourite stream. " +
                "You can also say Alexa stop to exit the skill")
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
            AlexaResponse response = null;
            var channel = "some streamer";

            if (intentRequest.Intent.Slots.Any())
            {
                channel = intentRequest.Intent.Slots["channel"].Value;
            }

            _logger.LogInformation($"User asked for: {channel}");

            var standardisedChannel = channel
                .Replace(" ", string.Empty)
                .Replace(".", string.Empty);

            var dbChannel = new Channel();

            var sql = $"SELECT * FROM Channels WHERE SOUNDEX(Name) = SOUNDEX(@standardisedChannel)";
            using (System.Data.IDbConnection connection = new SqlConnection(_dbSettings.DefaultConnection))
            {
                dbChannel = await connection.QuerySingleAsync<Channel>(sql, new { standardisedChannel });
            }

            if (dbChannel != null && !string.IsNullOrWhiteSpace(dbChannel.Name))
            {
                var name = dbChannel.Name;
                var id = dbChannel.Id;
                var sessions = new List<StreamSession>();
                var now = $"{DateTime.UtcNow.Year}-{DateTime.UtcNow.Month}-{DateTime.UtcNow.Day} " +
                    $"{DateTime.UtcNow.Hour}:{DateTime.UtcNow.Minute}:{DateTime.UtcNow.Second}";

                string query = $"SELECT * FROM StreamSessions WHERE ChannelId = @id AND UtcStartTime > @now ORDER BY UtcStartTime";
                using (System.Data.IDbConnection connection = new SqlConnection(_dbSettings.DefaultConnection))
                {
                    using (var multi = await connection.QueryMultipleAsync(query, new { id, now }))
                    {
                        sessions = (await multi.ReadAsync<StreamSession>()).ToList();
                    }
                }

                _logger.LogInformation($"Stream Sessions found: {sessions.Count}");
                _logger.LogInformation($"Next stream found: {sessions.FirstOrDefault()?.UtcStartTime.ToString() ?? "None"}");

                var nextStream = new StreamSession();
                var zonedDateTime = DateTime.MinValue;
                var nextStreamTimeFormatted = "currently has no future streams set up in the Dev Streams database";

                if (sessions.Count > 0)
                {
                    nextStream = sessions.FirstOrDefault();

                    if (nextStream != null)
                    {
                        _logger.LogInformation($"We found: {nextStream.UtcStartTime.ToString()}");

                        DateTimeZone zone = DateTimeZoneProviders.Tzdb[_userTimeZone];
                        zonedDateTime = nextStream.UtcStartTime.InZone(zone).ToDateTimeUnspecified();
                        nextStreamTimeFormatted = string.Format("will be streaming next on {0:dddd, MMMM dd} at {0:h:mm tt}", zonedDateTime, zonedDateTime);

                        _logger.LogInformation($"We found next stream on: {zonedDateTime.ToString("f")}");
                    }
                }
                               
                _logger.LogInformation($"We found: {name}");

                response = new ResponseBuilder()
                    .Say($"{channel} {nextStreamTimeFormatted}")
                    .Build();
            }
            else
            {
                _logger.LogInformation("We found no matches");

                response = new ResponseBuilder()
                    .Say($"Sorry, I cound not find {channel} in my database of live coding streamers")
                    .Build();
            }

            return response;
        }

        private async Task<AlexaResponse> WhoIsLiveResponseHandler(IntentRequest intentRequest)
        {
            List<Channel> channels = await _repo.GetAll<Channel>();
            List<string> channelNames = channels.Select(x => x.Name).ToList();
            var liveChannels = await _twitchService.GetLiveChannels(channelNames);

            // liveChannels = new List<string> { "codebase alpha", "dev chatter" };

            var response = GetLiveNowResponse(liveChannels);

            var jsonResponse = JsonConvert.SerializeObject(response);
            _logger.LogInformation($"{jsonResponse}");

            return response;
        }

        private AlexaResponse GetLiveNowResponse(List<string> liveChannels)
        {
            var response = new ResponseBuilder()
                .Say("None of the streamers in my database are currently broadcasting")
                .WriteSimpleCard("Streaming Now!", "None")
                .Build();

            if (liveChannels != null && liveChannels.Count > 0)
            {
                var firstFew = string.Join(", ", liveChannels.Take(3));

                var text1 = liveChannels.Count == 1
                    ? $"{liveChannels.First()} is broadcasting now."
                    : $"{liveChannels.Count} streamers are broadcasting now:";

                var text2 = string.Empty;

                if (liveChannels.Count == 2)
                {
                    text2 = $"{liveChannels[0]} and {liveChannels[1]}";
                }

                if (liveChannels.Count == 3)
                {
                    text2 = $"{liveChannels[0]}, {liveChannels[1]} and {liveChannels[2]}";
                }

                if (liveChannels.Count > 3)
                {
                    text2 = $"Here are the first three: {firstFew}";
                }

                response = new ResponseBuilder()
                    .Say($"{text1} {text2}")
                    .WriteSimpleCard("Streaming Now!", $"{firstFew}")
                    .Build();
            }

            return response;
        }
    }
}