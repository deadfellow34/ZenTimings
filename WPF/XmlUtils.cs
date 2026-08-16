using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace ZenTimings
{
    internal class XmlUtils
    {
        public static string SerializeToXml<T>(T obj)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(T));
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, obj);
                return writer.ToString();
            }
        }

        public static T DeserializeFromXml<T>(string xml)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(T));

            // A file this app wrote can hold a numeric character reference to a control character,
            // which the default reader rejects - the whole file is lost over one field. The reader
            // stays on the StreamReader: the documents declare utf-16 and are written as UTF-8, so
            // only a reader that ignores the declaration can open them.
            XmlReaderSettings settings = new XmlReaderSettings { CheckCharacters = false };

            using (StreamReader reader = new StreamReader(xml))
            using (XmlReader xmlReader = XmlReader.Create(reader, settings))
            {
                return (T)serializer.Deserialize(xmlReader);
            }
        }
    }
}
