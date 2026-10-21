/**
 * Alpha mail gateway for login/registration codes and invitation links. Deploy as a web app that executes as you.
 * Set ALPHA_OTP_SECRET in Script Properties; never place it in this file.
 */
function doPost(e) {
  try {
    // Invitations carry a Hebrew subject and text that the backend JSON-escapes (\uXXXX), so allow 4 KB.
    if (!e || !e.postData || e.postData.contents.length > 30000) return reply_(false);
    const body = JSON.parse(e.postData.contents);
    const secret = PropertiesService.getScriptProperties().getProperty('ALPHA_OTP_SECRET');
    if (!secret || secret.length < 32 || typeof body.secret !== 'string' || !sameSecret_(secret, body.secret))
      return reply_(false);
    if (typeof body.to !== 'string' || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(body.to) || body.to.length > 320)
      return reply_(false);

    if (body.type === 'manufacturer-inquiry') {
      if (typeof body.subject !== 'string' || body.subject.length < 1 || body.subject.length > 240 ||
          /[\r\n]/.test(body.subject) ||
          typeof body.text !== 'string' || body.text.length < 1 || body.text.length > 12000)
        return reply_(false);
      MailApp.sendEmail({ to: body.to, subject: body.subject, body: body.text, name: 'Alpha' });
      return reply_(true);
    }

    if (body.type === 'invitation') {
      // body.code is the invitation link: an https URL to the frontend's /invite page with a 64-hex token.
      if (typeof body.code !== 'string' || !/^https:\/\/[a-z0-9.-]+(:\d+)?\/invite\?token=[0-9a-f]{64}$/.test(body.code))
        return reply_(false);
      if (typeof body.subject !== 'string' || body.subject.length > 200 ||
          typeof body.text !== 'string' || body.text.length > 2000 || body.text.indexOf(body.code) < 0)
        return reply_(false);
      MailApp.sendEmail({ to: body.to, subject: body.subject, body: body.text, name: 'Alpha' });
      return reply_(true);
    }

    if (typeof body.code !== 'string' || !/^\d{6}$/.test(body.code)) return reply_(false);
    MailApp.sendEmail({
      to: body.to,
      subject: 'קוד כניסה למערכת Alpha',
      body: 'קוד הכניסה שלך: ' + body.code + '\nהקוד תקף ל-5 דקות.',
      name: 'Alpha'
    });
    return reply_(true);
  } catch (error) {
    // Do not log recipient addresses, codes, links, or the shared secret.
    console.error('Alpha mail delivery failed');
    return reply_(false);
  }
}

function sameSecret_(expected, received) {
  // Avoid early exits based on the first differing character.
  let difference = expected.length ^ received.length;
  for (let i = 0; i < Math.max(expected.length, received.length); i++)
    difference |= (expected.charCodeAt(i) || 0) ^ (received.charCodeAt(i) || 0);
  return difference === 0;
}

function reply_(ok) {
  return ContentService.createTextOutput(JSON.stringify({ ok: ok }))
    .setMimeType(ContentService.MimeType.JSON);
}
