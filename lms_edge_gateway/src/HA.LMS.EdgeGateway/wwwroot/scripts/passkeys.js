window.lmsEdgePasskeys = window.lmsEdgePasskeys || {};

window.lmsEdgePasskeys.enrollForUser = async (userId, friendlyName) => {
    return enrollWithOptionsEndpoint(`/api/passkeys/users/${encodeURIComponent(userId)}/enroll/options`, friendlyName);
};

window.lmsEdgePasskeys.enrollCurrentUser = async (friendlyName) => {
    return enrollWithOptionsEndpoint("/api/passkeys/me/enroll/options", friendlyName);
};

async function enrollWithOptionsEndpoint(optionsUrl, friendlyName) {
    if (!window.PublicKeyCredential) {
        return {
            succeeded: false,
            message: "This browser does not support passkeys."
        };
    }

    if (!window.isSecureContext) {
        return {
            succeeded: false,
            message: "Passkey setup needs HTTPS, or direct localhost access on this machine."
        };
    }

    try {
        const optionsResponse = await postJson(optionsUrl, {
            friendlyName
        });
        if (!optionsResponse.succeeded) {
            return {
                succeeded: false,
                message: optionsResponse.message ?? "Passkey setup could not start."
            };
        }

        const publicKey = prepareCredentialCreationOptions(optionsResponse.options);
        const credential = await navigator.credentials.create({ publicKey });
        if (!credential) {
            return {
                succeeded: false,
                message: "Passkey setup was cancelled."
            };
        }

        const completeResponse = await postJson("/api/passkeys/register/complete", {
            stateId: optionsResponse.stateId,
            credential: publicKeyCredentialToJson(credential)
        });

        return {
            succeeded: completeResponse.succeeded === true,
            message: completeResponse.message ?? (completeResponse.succeeded ? "Passkey added." : "Passkey setup failed.")
        };
    } catch (error) {
        return {
            succeeded: false,
            message: error instanceof Error ? error.message : "Passkey setup failed."
        };
    }
}

window.lmsEdgePasskeys.login = async (email, returnUrl) => {
    if (!window.PublicKeyCredential) {
        return {
            succeeded: false,
            message: "This browser does not support passkeys."
        };
    }

    if (!window.isSecureContext) {
        return {
            succeeded: false,
            message: "Device passkey sign-in needs HTTPS, or direct localhost access on this machine."
        };
    }

    if (!email || email.trim() === "") {
        return {
            succeeded: false,
            message: "Enter your email first."
        };
    }

    try {
        const optionsResponse = await postJson("/api/passkeys/login/options", {
            email: email.trim()
        });
        if (!optionsResponse.succeeded) {
            return {
                succeeded: false,
                message: optionsResponse.message ?? "No passkey is available for this email."
            };
        }

        const publicKey = prepareAssertionOptions(optionsResponse.options);
        const credential = await navigator.credentials.get({ publicKey });
        if (!credential) {
            return {
                succeeded: false,
                message: "Device passkey sign-in was cancelled."
            };
        }

        const completeResponse = await postJson(
            `/api/passkeys/login/complete?returnUrl=${encodeURIComponent(returnUrl || "/")}`,
            {
                stateId: optionsResponse.stateId,
                credential: publicKeyCredentialToJson(credential)
            });

        if (!completeResponse.succeeded) {
            return {
                succeeded: false,
                message: completeResponse.message ?? "Device passkey sign-in failed."
            };
        }

        window.location.assign(completeResponse.redirectUrl || "/");
        return {
            succeeded: true,
            message: "Signed in."
        };
    } catch (error) {
        return {
            succeeded: false,
            message: error instanceof Error ? error.message : "Device passkey sign-in failed."
        };
    }
};

async function postJson(url, body) {
    const response = await fetch(url, {
        method: "POST",
        headers: {
            "Content-Type": "application/json",
            "Accept": "application/json"
        },
        credentials: "same-origin",
        body: JSON.stringify(body)
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok) {
        throw new Error(result.message ?? `Request failed with HTTP ${response.status}.`);
    }

    return result;
}

function prepareCredentialCreationOptions(options) {
    options.challenge = base64UrlToBuffer(options.challenge);
    options.user.id = base64UrlToBuffer(options.user.id);
    options.excludeCredentials = (options.excludeCredentials ?? []).map(credential => ({
        ...credential,
        id: base64UrlToBuffer(credential.id)
    }));

    return options;
}

function prepareAssertionOptions(options) {
    options.challenge = base64UrlToBuffer(options.challenge);
    options.allowCredentials = (options.allowCredentials ?? []).map(credential => ({
        ...credential,
        id: base64UrlToBuffer(credential.id)
    }));

    return options;
}

function publicKeyCredentialToJson(credential) {
    const response = credential.response;
    return {
        id: credential.id,
        rawId: bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
            attestationObject: response.attestationObject
                ? bufferToBase64Url(response.attestationObject)
                : undefined,
            authenticatorData: response.authenticatorData
                ? bufferToBase64Url(response.authenticatorData)
                : undefined,
            clientDataJSON: bufferToBase64Url(response.clientDataJSON),
            signature: response.signature
                ? bufferToBase64Url(response.signature)
                : undefined,
            userHandle: response.userHandle
                ? bufferToBase64Url(response.userHandle)
                : undefined
        },
        clientExtensionResults: credential.getClientExtensionResults()
    };
}

function base64UrlToBuffer(value) {
    const padded = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(value.length + ((4 - value.length % 4) % 4), "=");
    const binary = window.atob(padded);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) {
        bytes[index] = binary.charCodeAt(index);
    }

    return bytes.buffer;
}

function bufferToBase64Url(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = "";
    for (const byte of bytes) {
        binary += String.fromCharCode(byte);
    }

    return window.btoa(binary)
        .replace(/\+/g, "-")
        .replace(/\//g, "_")
        .replace(/=+$/g, "");
}
