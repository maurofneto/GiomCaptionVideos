export const LOGIN_COOKIE = "__Host-rc_login";
export const LOGIN_LABEL = "login-v1";
export const LOGIN_TTL_SECONDS = 10 * 60;

export interface LoginState {
  state: string;
  nonce: string;
  verifier: string;
  exp: number;
}
