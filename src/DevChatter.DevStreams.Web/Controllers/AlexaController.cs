﻿using System;
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

            switch (alexaRequest.RequestBody.Type)
            {
                case "LaunchRequest":
                    var ssml = @"<speak>Welcome to the Dev Streams skill</speak>";
                    response = responseBuilder.SayWithSsml(ssml)
                        .Build();
                    break;
                case "IntentRequest":
                    response = IntentRequestHandler(alexaRequest);
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

        private AlexaResponse IntentRequestHandler(AlexaRequest alexaRequest)
        {
            var intentRequest = alexaRequest.RequestBody as IntentRequest;

            AlexaResponse response = null;

            if (intentRequest != null)
            {
                switch (intentRequest.Intent.Name)
                {
                    case "whenNextIntent":
                        response = WhenNextResponseHandler(intentRequest);
                        break;
                    case "whoIsLiveIntent":
                        response = WhoIsLiveResponseHandler(intentRequest);
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

        private AlexaResponse WhenNextResponseHandler(IntentRequest intentRequest)
        {
            var channel = "some streamer";

            if (intentRequest.Intent.Slots.Any())
            {
                channel = intentRequest.Intent.Slots["channel"].Value;
            }

            _logger.LogInformation($"User asked for: {channel}");

            var standardisedChannel = channel.Replace(" ", string.Empty);

            var dbChannel = new Channel();

            string sql = $"SELECT * FROM Channels WHERE SOUNDEX(Name) = SOUNDEX(@standardisedChannel)";
            using (System.Data.IDbConnection connection = new SqlConnection(_dbSettings.DefaultConnection))
            {
                dbChannel = connection.QuerySingle<Channel>(sql, new { standardisedChannel });
            }

            AlexaResponse response = null;

            if (dbChannel != null && !string.IsNullOrWhiteSpace(dbChannel.Name))
            {
                var name = dbChannel.Name;
                var id = dbChannel.Id;
                var sessions = new List<StreamSession>();

                string query = $"SELECT * FROM StreamSessions WHERE Id = @id ORDER BY UtcStartTime";
                using (System.Data.IDbConnection connection = new SqlConnection(_dbSettings.DefaultConnection))
                {
                    using (var multi = connection.QueryMultiple(query, new { id }))
                    {

                        sessions = multi.Read<StreamSession>().ToList();
                    }
                }


                var nextStream = new StreamSession();
                var zonedDateTime = DateTime.MinValue;

                if (sessions.Any())
                {
                    var now = DateTime.UtcNow;
                    nextStream = sessions.FirstOrDefault(s => s.UtcStartTime.ToUnixTimeTicks() > now.Ticks);

                    if (nextStream != null)
                    {
                        _logger.LogInformation($"We found: {nextStream.UtcStartTime.ToString()}");
                        var userZone = DateTimeZoneProviders.Tzdb[_userTimeZone];
                        zonedDateTime = nextStream.UtcStartTime.InZone(userZone).ToDateTimeUnspecified();

                        _logger.LogInformation($"We found next stream on: {zonedDateTime.ToString("f")}");
                    }
                }
                               
                _logger.LogInformation($"We found: {name}");

                response = new ResponseBuilder()
                    .Say($"You have asked about {channel} and they will be streaming next on {zonedDateTime.ToString("f")}")
                    .Build();
            }
            else
            {
                _logger.LogInformation("We found not matches");

                response = new ResponseBuilder()
                    .Say($"I cound not find {channel} in my database")
                    .Build();
            }

            return response;
        }

        private AlexaResponse WhoIsLiveResponseHandler(IntentRequest intentRequest)
        {
            // TODO: Do this better. Extract and remove duplication.
            List<Channel> channels = _repo.GetAll<Channel>().Result;
            List<string> channelNames = channels.Select(x => x.Name).ToList();
            var liveChannels = _twitchService.GetLiveChannels(channelNames).Result;

            foreach (var channel in liveChannels)
            {
                _logger.LogInformation($"Live now: {channel}");
            }
            
            var response = new ResponseBuilder()
                .Say($"{liveChannels.First()} is streaming now")
                .WriteSimpleCard("Streaming Now!", $"{liveChannels.First()}")
                .Build();

            var jsonResponse = JsonConvert.SerializeObject(response);
            _logger.LogInformation($"{jsonResponse}");

            return response;
        }
    }
}