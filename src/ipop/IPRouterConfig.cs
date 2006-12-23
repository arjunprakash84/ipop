using System;
using System.Text;
using System.Collections;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Ipop {
  public class IPRouterConfig {
    public string ipop_namespace;
    public string brunet_namespace;
    public string device;
    public string dht_media;
    [XmlArrayItem (typeof(string), ElementName = "transport")]
    public string [] RemoteTAs;
    public EdgeListener [] EdgeListeners;
    public string NodeAddress;
    public AddressInfo AddressData;
    [XmlArrayItem (typeof(string), ElementName = "Device")]
    public string [] DevicesToBind;
    public bool DhtDHCP;
  }

  public class AddressInfo {
    public string IPAddress;
    public string Netmask;
    public string DHCPServerAddress;
    public string Password;
  }

  public class EdgeListener {
    [XmlAttribute]
    public string type;
    public string port;
    public string port_high;
    public string port_low;
  }

  public class IPRouterConfigHandler {
    public static IPRouterConfig Read(string configFile) {
      XmlSerializer serializer = new XmlSerializer(typeof(IPRouterConfig));
      FileStream fs = new FileStream(configFile, FileMode.Open);
      IPRouterConfig config = (IPRouterConfig) serializer.Deserialize(fs);
      fs.Close();
      return config;
    }

    public static void Write(string configFile, 
      IPRouterConfig config) {
      FileStream fs = new FileStream(configFile, FileMode.Create, 
        FileAccess.Write);
      XmlSerializer serializer = new XmlSerializer(typeof(IPRouterConfig));
      serializer.Serialize(fs, config);
      fs.Close();
    }
  }
}