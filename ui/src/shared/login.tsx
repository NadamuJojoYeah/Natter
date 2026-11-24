import { useState } from "react";
import { Alert, Button, Card, Form, Input, Typography } from "antd";

export interface LoginPromptProps {
  title: string;
  hint: string;
  error?: string | null;
  busy?: boolean;
  onSubmit: (token: string) => void;
}

/**
 * Explicit re-authentication (task-65 requirement): a 401 never blanks the console, it shows this
 * form with the reason and the error code.
 */
export function LoginPrompt({ title, hint, error, busy, onSubmit }: LoginPromptProps) {
  const [token, setToken] = useState("");

  return (
    <Card style={{ maxWidth: 520, margin: "48px auto" }}>
      <Typography.Title level={4} style={{ marginTop: 0 }}>
        {title}
      </Typography.Title>
      <Typography.Paragraph type="secondary">{hint}</Typography.Paragraph>
      {error ? <Alert type="error" showIcon message=<code>{error}</code> style={{ marginBottom: 12 }} /> : null}
      <Form
        layout="vertical"
        onFinish={() => {
          if (token.trim()) {
            onSubmit(token.trim());
          }
        }}
      >
        <Form.Item label="Token" required>
          <Input.Password
            autoFocus
            value={token}
            onChange={(event) => setToken(event.target.value)}
            placeholder="WEBCONSOLE_TOKEN / 会话令牌"
          />
        </Form.Item>
        <Button type="primary" htmlType="submit" loading={busy} block>
          登录
        </Button>
      </Form>
    </Card>
  );
}
