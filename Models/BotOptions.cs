using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vkadmin_msg.Models
{
    public class BotOptions
    {
        public TelegramBotConfig TelegramBot { get; set; } = new TelegramBotConfig();
        public VkConfig Vk { get; set; } = new VkConfig();
        public string DataMapFilePath { get; set; } = "datamap.json";
    }

    public class TelegramBotConfig
    {
        public string TgToken { get; set; } = string.Empty;
        public long AllowedGroupId { get; set; } = 0;
        public bool AllowReply { get; set; } = false;
        public string SecondTgToken { get; set; } = string.Empty;
    }

    public class VkConfig
    {
        public string KateMobileToken { get; set; } = string.Empty;
        public long VkGroupId { get; set; } = 0;
        public string VkGroupToken { get; set; } = string.Empty;
        public bool AllowVkBot { get; set; } = false;
        public VkBot VkBotConfig { get; set;} = new VkBot();
        
    }

    public class VkBot 
    {
        public string VkResponsesPath { get; set; } = "vk_responses.json";
        public string VkKeyboardsPath { get; set; } = "vk_keyboards.json";
    }
}
